using System.CommandLine;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;

namespace OmniSpot.Benchmarking.Ntfs;

internal static class LiveCatalogProbeCommand
{
    internal static Command CreateCommand()
    {
        var command = new Command("live-catalog-probe", "Keşif sürerken aranabilir, ayrı katalog deneyi.");
        var root = new Option<string>("--root") { Required = true };
        var output = new Option<string>("--output-directory") { Required = true };
        var replay = new Option<string?>("--replay-database");
        var catalog = new Option<string?>("--catalog-path");
        var mode = new Option<string>("--mode") { Required = true };
        var limit = new Option<int>("--limit");
        var profile = new Option<bool>("--allocation-profile");
        var adapter = new Option<bool>("--adapter");
        command.Options.Add(root); command.Options.Add(output); command.Options.Add(replay); command.Options.Add(catalog); command.Options.Add(mode); command.Options.Add(limit); command.Options.Add(profile); command.Options.Add(adapter);
        command.SetAction((args, ct) => Run(args.GetValue(root)!, args.GetValue(output)!, args.GetValue(replay), args.GetValue(catalog), args.GetValue(mode)!, args.GetValue(limit), args.GetValue(profile), args.GetValue(adapter), ct));
        return command;
    }
    private static async Task<int> Run(string root, string output, string? replay, string? catalog, string mode, int limit, bool profile, bool adapter, CancellationToken ct)
    {
        output = Path.GetFullPath(output); root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        if (mode is not ("packed" or "live" or "live-files-first" or "filesystem" or "open-packed" or "open-live") ||
            Directory.Exists(output) || File.Exists(output) || output.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || limit < 0 ||
            (mode != "filesystem" && !File.Exists(replay)) || profile && mode is not ("live" or "live-files-first") || adapter && mode is ("packed" or "open-packed")) throw new ArgumentException("Geçerli kaynak ve kökün dışında yeni çıktı dizini gerekli.");
        Directory.CreateDirectory(output);
        var phases = new List<object>(); var start = Stopwatch.GetTimestamp();
        void Phase(string phase, int count) { var ms = Stopwatch.GetElapsedTime(start).TotalMilliseconds; phases.Add(new { Phase = phase, Milliseconds = ms, Count = count }); Console.WriteLine($"{phase}: {ms / 1000:F2}s ({count})"); }
        var before = GC.GetTotalAllocatedBytes(true); using var sampler = new Sampler();
        var result = mode switch
        {
            "packed" => BuildPacked(output, replay!, limit, Phase, ct),
            "open-packed" => OpenPacked(catalog!, Phase),
            "open-live" => OpenLive(catalog!, Phase),
            _ => BuildLive(root, output, replay, mode, limit, profile, Phase, ct)
        };
        if (adapter && result.Errors.Length == 0) result = result with { State = result.Live!.CreateSearchState() };
        var duration = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var allocations = GC.GetTotalAllocatedBytes(true) - before;
        sampler.Stop(); var immediate = Point.Read();
        await Task.Delay(100, ct); FullGc(); var ready = Point.Read();
        Phase("validate", result.State.ItemCount);
        var validation = Validate(result, replay, limit, ct);
        var afterValidation = Point.Read();
        await Task.Delay(100, ct); FullGc(); var warmed = Point.Read();
        var disk = Directory.GetFiles(output, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length);
        var report = new
        {
            Classification = "exploratory", Mode = mode, Adapter = adapter, Root = root, Replay = replay, Limit = limit,
            FormatContract = PackedCheckpoint.CurrentContract, LiveUsedBytes = result.Live?.UsedBytes,
            AllocationProfile = result.AllocationProfile,
            Completed = result.Errors.Length == 0, result.Errors, result.State.ItemCount, result.State.TokenCount,
            TotalMilliseconds = duration, AllocatedBytes = allocations, Peak = sampler.Peak, Immediate = immediate,
            ReadyAfterFullGc = ready, AfterValidation = afterValidation, WarmAfterFullGc = warmed,
            DiskBytes = disk, DiskBytesPerEntry = disk / (double)result.State.ItemCount,
            RuntimeGcConfiguration = GC.GetConfigurationVariables(), Phases = phases, Validation = validation,
            SourceCatalogBytes = catalog is null ? (long?)null : Directory.Exists(catalog)
                ? Directory.GetFiles(catalog, "*", SearchOption.AllDirectories).Sum(path => new FileInfo(path).Length) : new FileInfo(catalog).Length,
            Notes = new[] { "Ready and warmed memory are separate; mapped pages touched by validation can increase resident memory.",
                "Seal flush/hash/manifest is timed; no global posting sort or second catalog assembly in live mode.",
                "Frozen replay does not measure physical NTFS discovery. Filesystem mode uses existing scoped walker.",
                "Live snapshots support initial ingestion, not production incremental USN mutation. Unsealed data is not a completed index." }
        };
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report, new JsonSerializerOptions { WriteIndented = true }), ct);
        GC.KeepAlive(result.Owner); GC.KeepAlive(result.State);
        Console.WriteLine($"Completed={report.Completed}; {duration / 1000:F2}s; {disk / 1048576.0:F1} MiB; ready {ready.WorkingSet / 1048576.0:F1} MiB; warmed {warmed.WorkingSet / 1048576.0:F1} MiB");
        result.Live?.Dispose();
        return result.Errors.Length == 0 ? 0 : 1;
    }
    private static void FullGc() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private sealed record BuildResult(IQueryCatalogSnapshot State, object Owner, PackedCatalog? Packed = null, LiveCatalog? Live = null, string[]? CaptureErrors = null, object? AllocationProfile = null)
    { internal string[] Errors => CaptureErrors ?? []; }
    private static PackedRecord Record(IndexManager.HierarchyProbeEntry entry) => new(entry.Item, entry.ModifiedUtc, entry.CreatedUtc, entry.Hidden, entry.System);
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BuildResult BuildPacked(string output, string replay, int limit, Action<string, int> phase, CancellationToken ct)
    {
        WritePacked(output, replay, limit, phase, ct); FullGc(); phase("builder_gc_complete", 0);
        var catalog = PackedCatalog.Open(Path.Combine(output, "catalog.bin")); phase("ready", catalog.ItemCount);
        return new(CompactSearchState.FromCatalog(catalog), catalog, catalog);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void WritePacked(string output, string replay, int limit, Action<string, int> phase, CancellationToken ct)
    {
        using var builder = new PackedCatalogBuilder(Path.Combine(output, "scratch"), new BasicTokenizer());
        var directories = new Dictionary<string, (int Id, string Path)>(StringComparer.OrdinalIgnoreCase);
        phase("ingest", 0);
        foreach (var entry in HierarchyProbeStore.Read(replay).Take(limit == 0 ? int.MaxValue : limit))
        {
            ct.ThrowIfCancellationRequested(); var parent = (-1, (string?)null);
            if (!string.IsNullOrEmpty(entry.Item.ParentPath)) parent = directories[entry.Item.ParentPath];
            var id = builder.Add(Record(entry), parent.Item1, parent.Item2);
            if (entry.Item.IsDirectory) directories.Add(entry.Item.FullPath, (id, entry.Item.FullPath));
        }
        phase("finalize", builder.Count); builder.WriteNew(Path.Combine(output, "catalog.bin"), ct: ct); phase("written", builder.Count);
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BuildResult BuildLive(string root, string output, string? replay, string mode, int limit, bool profile, Action<string, int> phase, CancellationToken ct)
    {
        var live = new LiveCatalog(Path.Combine(output, "catalog"), root); var count = 0;
        var stages = new long[5]; long last = 0, sourceBytes = 0, sinkBytes = 0, sealBytes = 0;
        if (profile) live.AllocationCheckpoint = stage =>
        {
            var now = GC.GetAllocatedBytesForCurrentThread();
            if (stage != LiveBuildStage.Begin) stages[(int)stage] += now - last;
            last = now;
        };
        void Accept(IndexManager.HierarchyProbeEntry entry)
        {
            ct.ThrowIfCancellationRequested(); var id = live.Add(Record(entry)); count++;
            if (count == 1 || count % 100_000 == 0)
            {
                var snapshot = live.Snapshot();
                if (snapshot.GetRecord(id)?.Item != entry.Item) throw new InvalidDataException("Live kayıt anında okunamıyor.");
                if (count == 1)
                {
                    var token = new BasicTokenizer().Tokenize(entry.Item.Name).FirstOrDefault();
                    if (token is not null && !snapshot.Get(token).Any(item => item == entry.Item)) throw new InvalidDataException("Live ilk kayıt aranabilir değil.");
                }
                phase(count == 1 ? "first_searchable" : "searchable_checkpoint", count);
            }
        }
        phase("ingest", 0); string[] errors = [];
        try
        {
            if (mode == "filesystem")
            {
                using var manager = IndexManager.CreateWithDatabasePath(Path.Combine(output, "unused.db"), enforceMeasurementPathSafety: false, layout: SearchStateLayout.Compact);
                errors = manager.CaptureHierarchyProbe(root, Accept, ct);
            }
            else if (profile)
            {
                using var input = HierarchyProbeStore.Read(replay!, filesFirst: mode == "live-files-first").GetEnumerator();
                while (limit == 0 || count < limit)
                {
                    var at = GC.GetAllocatedBytesForCurrentThread(); var more = input.MoveNext();
                    sourceBytes += GC.GetAllocatedBytesForCurrentThread() - at;
                    if (!more) break;
                    at = GC.GetAllocatedBytesForCurrentThread(); Accept(input.Current);
                    sinkBytes += GC.GetAllocatedBytesForCurrentThread() - at;
                }
            }
            else foreach (var entry in HierarchyProbeStore.Read(replay!, filesFirst: mode == "live-files-first").Take(limit == 0 ? int.MaxValue : limit)) Accept(entry);
            phase("all_searchable", count);
            if (errors.Length == 0)
            {
                var at = profile ? GC.GetAllocatedBytesForCurrentThread() : 0;
                live.Seal(); if (profile) sealBytes = GC.GetAllocatedBytesForCurrentThread() - at;
                phase("sealed", count);
            }
            live.AllocationCheckpoint = null;
            return new(live.Snapshot(), live, Live: live, CaptureErrors: errors, AllocationProfile: profile ? new
            {
                SourceRead = sourceBytes, SinkTotal = sinkBytes, Placement = stages[1], Metadata = stages[2],
                Tokens = stages[3], Publication = stages[4], AdapterAndCheckpointQueries = sinkBytes - stages.Sum(), Seal = sealBytes,
                Notes = "Synchronous current-thread stage attribution; instrumented timing is not the final performance result. SinkTotal contains the following stage values."
            } : null);
        }
        catch { live.Dispose(); throw; }
    }
    private static BuildResult OpenPacked(string path, Action<string, int> phase)
    { var catalog = PackedCatalog.Open(path); phase("ready", catalog.ItemCount); return new(CompactSearchState.FromCatalog(catalog), catalog, catalog); }
    private static BuildResult OpenLive(string path, Action<string, int> phase)
    { var catalog = LiveCatalog.Open(path); var state = catalog.Snapshot(); phase("ready", state.ItemCount); return new(state, catalog, Live: catalog); }
    private static object Validate(BuildResult result, string? replay, int limit, CancellationToken ct)
    {
        var count = 0; var digest = new ulong[4]; var liveSnapshot = result.Live?.Snapshot();
        if (replay is not null)
        {
            foreach (var entry in HierarchyProbeStore.Read(replay).Take(limit == 0 ? int.MaxValue : limit))
            {
                ct.ThrowIfCancellationRequested();
                PackedRecord? record = result.Packed is { } packed ? packed.GetRecord(packed.Find(entry.Item.FullPath))
                    : liveSnapshot!.FindRecord(entry.Item.FullPath);
                if (record is null || record.Item != entry.Item || record.ModifiedUtc != entry.ModifiedUtc || record.CreatedUtc != entry.CreatedUtc || record.Hidden != entry.Hidden || record.System != entry.System)
                    throw new InvalidDataException("Live/frozen tam metadata uyuşmuyor: " + entry.Item.FullPath);
                var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new { entry.Item, entry.ModifiedUtc, entry.CreatedUtc, entry.Hidden, entry.System }));
                for (var i = 0; i < 4; i++) digest[i] = unchecked(digest[i] + BitConverter.ToUInt64(hash, i * 8));
                count++;
            }
            if (count != result.State.ItemCount) throw new InvalidDataException("Live öğe sayısı uyuşmuyor.");
        }
        var queries = new[] { ("exact", "txt"), ("exact", "pdf"), ("exact", "readme"), ("partial", "readm"), ("fuzzy", "readmx") }
            .Select(test =>
            {
                var allocated = GC.GetTotalAllocatedBytes(true); var start = Stopwatch.GetTimestamp();
                var state = result.State.ForQuery();
                var items = test.Item1 switch { "exact" => state.Get(test.Item2), "partial" => state.GetPartial(test.Item2), _ => state.GetFuzzy(test.Item2, 1) };
                var elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds; var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var item in items.OrderBy(item => item.FullPath, StringComparer.Ordinal)) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
                return new { Kind = test.Item1, Query = test.Item2, items.Count, Milliseconds = elapsed, AllocatedBytes = bytes, Sha256 = Convert.ToHexString(hash.GetHashAndReset()) };
            }).ToArray();
        return new { Count = count, CanonicalDigest = string.Concat(digest.Select(value => value.ToString("X16"))), Queries = queries };
    }
    private sealed record Point(long WorkingSet, long Private, long Managed, long GcCommitted)
    {
        internal static Point Read() { using var p = Process.GetCurrentProcess(); return new(p.WorkingSet64, p.PrivateMemorySize64, GC.GetTotalMemory(false), GC.GetGCMemoryInfo().TotalCommittedBytes); }
    }
    private sealed class Sampler : IDisposable
    {
        private readonly Timer _timer; private readonly object _gate = new();
        internal Point Peak { get; private set; } = Point.Read();
        internal Sampler() => _timer = new(_ => Sample(), null, 0, 100);
        private void Sample() { lock (_gate) { var point = Point.Read(); Peak = new(Math.Max(Peak.WorkingSet, point.WorkingSet), Math.Max(Peak.Private, point.Private), Math.Max(Peak.Managed, point.Managed), Math.Max(Peak.GcCommitted, point.GcCommitted)); } }
        internal void Stop() { _timer.DisposeAsync().AsTask().GetAwaiter().GetResult(); Sample(); }
        public void Dispose() => _timer.Dispose();
    }
}
