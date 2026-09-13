using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;

namespace OmniSpot.Benchmarking.Ntfs;

internal static class HierarchyProbeCommand
{
    internal static Command CreateCommand()
    {
        var command = new Command("hierarchy-probe", "Ayrı yeni depoda exploratory streaming hiyerarşi karşılaştırması.");
        var root = new Option<string>("--root") { Required = true };
        var output = new Option<string>("--output-directory") { Required = true };
        var mode = new Option<string>("--mode") { Required = true };
        var replay = new Option<string?>("--replay-database");
        var sourceCatalog = new Option<string?>("--catalog-path");
        command.Options.Add(root); command.Options.Add(output); command.Options.Add(mode); command.Options.Add(replay); command.Options.Add(sourceCatalog);
        command.SetAction((result, ct) => RunAsync(result.GetValue(root)!, result.GetValue(output)!,
            result.GetValue(mode)!, result.GetValue(replay), result.GetValue(sourceCatalog), ct));
        return command;
    }

    private static async Task<int> RunAsync(string root, string output, string mode, string? replay, string? sourceCatalog, CancellationToken ct)
    {
        root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        output = Path.GetFullPath(output);
        if (mode is not ("baseline" or "streaming" or "packed" or "reopen" or "reopen-streaming") || Directory.Exists(output) || File.Exists(output) ||
            output.Equals(root, StringComparison.OrdinalIgnoreCase) ||
            output.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
            (replay is null && !Directory.Exists(root)) || (replay is not null && !File.Exists(replay)) ||
            (mode is "packed" or "reopen" or "reopen-streaming" && replay is null) ||
            (mode is "reopen" or "reopen-streaming" && !File.Exists(sourceCatalog)))
            throw new ArgumentException("Yeni, kökün dışında çıktı dizini ve geçerli kaynak gerekli; packed/reopen için frozen replay veritabanı gerekli.");
        Directory.CreateDirectory(output);
        var phases = new ConcurrentQueue<object>();
        var started = Stopwatch.GetTimestamp();
        void Phase(string name, int count = 0)
        {
            var seconds = Stopwatch.GetElapsedTime(started).TotalSeconds;
            phases.Enqueue(new { Name = name, Seconds = seconds, Count = count });
            Console.WriteLine($"{name}: {seconds:F2}s ({count})");
        }
        var allocationStart = GC.GetTotalAllocatedBytes(true);
        using var memory = new MemorySampler();
        var result = mode switch
        {
            "baseline" => await BaselineAsync(root, output, replay, Phase, ct),
            "streaming" => Streaming(root, output, replay, Phase, ct),
            "packed" => Packed(output, replay!, Phase, ct),
            "reopen-streaming" => ReopenStreaming(sourceCatalog!, Phase),
            _ => Reopen(sourceCatalog!, Phase)
        };
        var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
        var allocations = GC.GetTotalAllocatedBytes(true) - allocationStart;
        memory.Stop();
        var immediate = MemorySampler.Read();
        await Task.Delay(100, ct);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        var retained = MemorySampler.Read();
        var database = Path.Combine(output, "index.db");
        var catalog = Path.Combine(output, "catalog.bin");
        var files = Directory.GetFiles(output, "*", SearchOption.TopDirectoryOnly)
            .Select(path => new { Name = Path.GetFileName(path), Bytes = new FileInfo(path).Length }).ToArray();
        Phase("validation");
        var validation = Validate(result.State, result.Catalog is null && mode != "reopen-streaming" ? database : replay!, mode == "streaming", result.Catalog);
        var report = new
        {
            Classification = "exploratory", Mode = mode, Source = replay is null ? "filesystem" : "frozen-database-replay",
            Root = root, ReplayDatabase = replay, Completed = result.Errors.Length == 0, Errors = result.Errors,
            TotalMilliseconds = elapsed, AllocatedBytes = allocations, result.State.ItemCount, result.State.TokenCount,
            DiskBytes = files.Sum(file => file.Bytes), BytesPerEntry = (double)files.Sum(file => file.Bytes) / result.State.ItemCount,
            Files = files, Peak = memory.Peak, Immediate = immediate, RetainedAfterFullGc = retained,
            SourceCatalogBytes = sourceCatalog is null ? (long?)null : new FileInfo(sourceCatalog).Length,
            RuntimeGcConfiguration = GC.GetConfigurationVariables(),
            Phases = phases.ToArray(), Validation = validation,
            Limitations = new[] { "Fresh construction only; service/watcher disabled", "Prototype SQL is not production-compatible",
                "No OS filesystem-cache reset", "Managed bytes are not total process RAM", "Retained memory measured after async unwind, 100ms idle and full GC",
                "Builder temporarily retains token dictionary and integer relation arrays", "Packed/reopen replay measures catalog construction/loading, not NTFS discovery" }
        };
        await File.WriteAllTextAsync(Path.Combine(output, "result.json"), JsonSerializer.Serialize(report,
            new JsonSerializerOptions { WriteIndented = true }), ct);
        GC.KeepAlive(result.State);
        Console.WriteLine($"Completed: {elapsed / 1000:F2}s; {report.DiskBytes / 1048576.0:F1} MiB; retained managed {retained.Managed / 1048576.0:F1} MiB");
        return result.Errors.Length == 0 ? 0 : 1;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static async Task<BuildResult> BaselineAsync(string root, string output, string? replay,
        Action<string, int> phase, CancellationToken ct)
    {
        using var manager = IndexManager.CreateWithDatabasePath(Path.Combine(output, "index.db"),
            enforceMeasurementPathSafety: false, layout: SearchStateLayout.Compact);
        var errors = new List<string>();
        var lastPhase = "";
        manager.OnError += errors.Add;
        manager.OnProgress += progress =>
        {
            if (progress.Phase is { } next && next != lastPhase)
            { lastPhase = next; phase(next, 0); }
        };
        phase("begin", 0);
        errors.AddRange(await manager.BootstrapHierarchyProbeAsync(root,
            replay is null ? null : HierarchyProbeStore.Read(replay), ct));
        var state = (CompactSearchState)manager.CurrentSearchState;
        phase("write_catalog", state.ItemCount);
        state.WriteNewBase(Path.Combine(output, "catalog.bin"));
        phase("ready", state.ItemCount);
        return new(state, errors.ToArray());
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BuildResult Streaming(string root, string output, string? replay,
        Action<string, int> phase, CancellationToken ct)
    {
        using var manager = IndexManager.CreateWithDatabasePath(Path.Combine(output, "unused.db"),
            enforceMeasurementPathSafety: false, layout: SearchStateLayout.Compact);
        using var store = new HierarchyProbeStore(Path.Combine(output, "index.db"), root);
        using var builder = new StreamingCatalogBuilder(Path.Combine(output, "scratch"), new BasicTokenizer());
        var directories = new Dictionary<string, (int Id, string Path)>(StringComparer.OrdinalIgnoreCase);
        void Accept(IndexManager.HierarchyProbeEntry entry)
        {
            ct.ThrowIfCancellationRequested();
            var parent = (-1, (string?)null);
            if (!string.IsNullOrEmpty(entry.Item.ParentPath))
            {
                if (!directories.TryGetValue(entry.Item.ParentPath, out var found))
                    throw new InvalidDataException("Ebeveyn önce gelmeli: " + entry.Item.ParentPath);
                parent = (found.Id, found.Path);
            }
            var id = builder.Add(entry.Item, parent.Item1, parent.Item2);
            store.Add(entry, id, parent.Item1);
            if (entry.Item.IsDirectory) directories.Add(entry.Item.FullPath, (id, entry.Item.FullPath));
        }
        phase("acquire_and_store", 0);
        string[] errors = [];
        if (replay is null) errors = manager.CaptureHierarchyProbe(root, Accept, ct);
        else foreach (var entry in HierarchyProbeStore.Read(replay)) Accept(entry);
        phase("finalize_catalog", builder.Count);
        var state = builder.Complete(Path.Combine(output, "catalog.bin"), ct);
        phase("commit", builder.Count);
        store.Complete();
        phase("ready", builder.Count);
        return new(state, errors);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BuildResult Packed(string output, string replay, Action<string, int> phase, CancellationToken ct)
    {
        var count = WritePacked(output, replay, phase, ct);
        phase("release_builder", count);
        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: true, compacting: true);
        GC.WaitForPendingFinalizers();
        phase("open_catalog", count);
        var catalog = PackedCatalog.Open(Path.Combine(output, "catalog.bin"));
        phase("ready", count);
        return new(CompactSearchState.FromCatalog(catalog), [], catalog);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static int WritePacked(string output, string replay, Action<string, int> phase, CancellationToken ct)
    {
        using var builder = new PackedCatalogBuilder(Path.Combine(output, "scratch"), new BasicTokenizer());
        var directories = new Dictionary<string, (int Id, string Path)>(StringComparer.OrdinalIgnoreCase);
        phase("acquire_and_store", 0);
        foreach (var entry in HierarchyProbeStore.Read(replay))
        {
            ct.ThrowIfCancellationRequested();
            var parent = (-1, (string?)null);
            if (!string.IsNullOrEmpty(entry.Item.ParentPath))
            {
                if (!directories.TryGetValue(entry.Item.ParentPath, out var found)) throw new InvalidDataException("Ebeveyn önce gelmeli.");
                parent = (found.Id, found.Path);
            }
            var id = builder.Add(new(entry.Item, entry.ModifiedUtc, entry.CreatedUtc, entry.Hidden, entry.System), parent.Item1, parent.Item2);
            if (entry.Item.IsDirectory) directories.Add(entry.Item.FullPath, (id, entry.Item.FullPath));
        }
        phase("finalize_catalog", builder.Count);
        builder.WriteNew(Path.Combine(output, "catalog.bin"), ct: ct);
        return builder.Count;
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BuildResult Reopen(string path, Action<string, int> phase)
    {
        phase("open_catalog", 0);
        var catalog = PackedCatalog.Open(path);
        phase("ready", catalog.ItemCount);
        return new(CompactSearchState.FromCatalog(catalog), [], catalog);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static BuildResult ReopenStreaming(string path, Action<string, int> phase)
    {
        phase("open_catalog", 0);
        var state = CompactSearchState.OpenMapped(path);
        phase("ready", state.ItemCount);
        return new(state, []);
    }

    private static object Validate(CompactSearchState state, string database, bool normalized, PackedCatalog? packed = null)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = database, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = "PRAGMA quick_check;";
        var integrity = command.ExecuteScalar()?.ToString();
        command.CommandText = "PRAGMA foreign_key_check;";
        using (var fk = command.ExecuteReader()) if (fk.Read()) throw new InvalidDataException("Foreign key violation");
        var digest = new ulong[4];
        var count = 0;
        foreach (var entry in HierarchyProbeStore.Read(database, normalized))
        {
            if (!state.TryGetItem(entry.Item.FullPath, out var item) || item != entry.Item)
                throw new InvalidDataException("SQL/catalog metadata mismatch: " + entry.Item.FullPath);
            if (packed is not null)
            {
                var record = packed.GetRecord(packed.Find(entry.Item.FullPath));
                if (record.ModifiedUtc != entry.ModifiedUtc || record.CreatedUtc != entry.CreatedUtc ||
                    record.Hidden != entry.Hidden || record.System != entry.System || record.IndexedUtc <= 0)
                    throw new InvalidDataException("Packed canonical metadata mismatch: " + entry.Item.FullPath);
            }
            var hash = SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
            { entry.Item, entry.ModifiedUtc, entry.CreatedUtc, entry.Hidden, entry.System }));
            for (var i = 0; i < 4; i++) digest[i] = unchecked(digest[i] + BitConverter.ToUInt64(hash, i * 8));
            count++;
        }
        if (count != state.ItemCount || integrity != "ok") throw new InvalidDataException("Kayıt sayısı/integrity uyuşmuyor.");
        var queries = new[] { "txt", "pdf", "jpg", "readme", "index", "dart", "flutter", "resim" }
            .Select(query =>
            {
                var started = Stopwatch.GetTimestamp();
                var items = state.Get(query);
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var item in items.OrderBy(item => item.FullPath, StringComparer.Ordinal))
                    hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
                return new { Query = query, items.Count, Milliseconds = ms, Sha256 = Convert.ToHexString(hash.GetHashAndReset()) };
            }).ToArray();
        var algorithms = new[] { (Kind: "exact", Query: "readme"), (Kind: "partial", Query: "readm"), (Kind: "fuzzy", Query: "readmx") }
            .Select(test =>
            {
                var allocated = GC.GetTotalAllocatedBytes(true); var started = Stopwatch.GetTimestamp();
                var items = test.Kind switch { "exact" => state.Get(test.Query), "partial" => state.GetPartial(test.Query), _ => state.GetFuzzy(test.Query, 1) };
                var ms = Stopwatch.GetElapsedTime(started).TotalMilliseconds;
                var bytes = GC.GetTotalAllocatedBytes(true) - allocated;
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                foreach (var item in items.OrderBy(item => item.FullPath, StringComparer.Ordinal)) hash.AppendData(JsonSerializer.SerializeToUtf8Bytes(item));
                return new { test.Kind, test.Query, items.Count, Milliseconds = ms, AllocatedBytes = bytes, Sha256 = Convert.ToHexString(hash.GetHashAndReset()) };
            }).ToArray();
        return new { Integrity = integrity, Count = count, CanonicalDigest = string.Concat(digest.Select(value => value.ToString("X16"))), Queries = queries, Algorithms = algorithms };
    }

    private sealed record BuildResult(CompactSearchState State, string[] Errors, PackedCatalog? Catalog = null);

    private sealed record MemoryPoint(long WorkingSet, long Private, long Managed, long GcCommitted);
    private sealed class MemorySampler : IDisposable
    {
        private readonly object _gate = new();
        private readonly Timer _timer;
        internal MemoryPoint Peak { get; private set; } = Read();
        internal MemorySampler() => _timer = new Timer(_ => Sample(), null, 0, 100);
        internal static MemoryPoint Read()
        {
            using var process = Process.GetCurrentProcess();
            return new(process.WorkingSet64, process.PrivateMemorySize64, GC.GetTotalMemory(false), GC.GetGCMemoryInfo().TotalCommittedBytes);
        }
        private void Sample()
        {
            lock (_gate)
            {
                var point = Read();
                Peak = new(Math.Max(Peak.WorkingSet, point.WorkingSet), Math.Max(Peak.Private, point.Private),
                    Math.Max(Peak.Managed, point.Managed), Math.Max(Peak.GcCommitted, point.GcCommitted));
            }
        }
        internal void Stop() { _timer.DisposeAsync().AsTask().GetAwaiter().GetResult(); Sample(); }
        public void Dispose() => _timer.Dispose();
    }
}
