using System.Collections.Concurrent;
using System.CommandLine;
using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text.Json;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;

namespace OmniSpot.Benchmarking.Ntfs;

internal static class DirectoryProbeCommand
{
    internal static Command CreateCommand()
    {
        var command = new Command("directory-probe", "Seçili klasörlerin production walker'ını yeni test indeksinde exploratory olarak ölçer.");
        var roots = new Option<string[]>("--root") { AllowMultipleArgumentsPerToken = true };
        var productionRoots = new Option<bool>("--omnispot-roots");
        var database = new Option<string>("--database") { Required = true, Description = "Henüz bulunmayan test index.db yolu." };
        var output = new Option<string>("--output") { Required = true, Description = "Yeni JSON çıktı yolu." };
        var catalog = new Option<string?>("--catalog-output") { Description = "İsteğe bağlı yeni katalog dosyası ve Core bellek/sorgu incelemesi." };
        command.Options.Add(roots);
        command.Options.Add(productionRoots);
        command.Options.Add(database);
        command.Options.Add(output);
        command.Options.Add(catalog);
        command.SetAction((result, ct) => RunAsync(result.GetValue(roots) ?? [],
            result.GetValue(productionRoots), result.GetValue(database)!, result.GetValue(output)!, result.GetValue(catalog), ct));
        return command;
    }

    private static async Task<int> RunAsync(string[] requestedRoots, bool productionRoots,
        string databasePath, string outputPath, string? catalogPath, CancellationToken ct)
    {
        if (productionRoots == (requestedRoots.Length > 0))
        {
            Console.Error.WriteLine("Yalnız --omnispot-roots veya --root seçin.");
            return 2;
        }
        var roots = (productionRoots ? new IndexedLocationProvider().Resolve().RootPaths : requestedRoots)
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        databasePath = Path.GetFullPath(databasePath);
        outputPath = Path.GetFullPath(outputPath);
        catalogPath = catalogPath is null ? null : Path.GetFullPath(catalogPath);
        if (roots.Length == 0 || roots.Any(root => !Directory.Exists(root)) ||
            File.Exists(databasePath) || File.Exists(outputPath) ||
            string.Equals(databasePath, outputPath, StringComparison.OrdinalIgnoreCase) ||
            roots.Any(root => IsWithin(databasePath, root) || IsWithin(outputPath, root)) ||
            (catalogPath is not null && (File.Exists(catalogPath) ||
                string.Equals(catalogPath, databasePath, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(catalogPath, outputPath, StringComparison.OrdinalIgnoreCase) ||
                roots.Any(root => IsWithin(catalogPath, root)))))
        {
            Console.Error.WriteLine("Kökler mevcut olmalı; DB/çıktı yeni dosyalar ve taranan köklerin dışında olmalı.");
            return 2;
        }

        using var outputFile = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
        using (new FileStream(databasePath, FileMode.CreateNew, FileAccess.Write)) { }
        var phases = new ConcurrentQueue<Phase>();
        var errors = new ConcurrentQueue<string>();
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allocatedBefore = GC.GetTotalAllocatedBytes(precise: true);
        var collectionsBefore = Enumerable.Range(0, 3).Select(GC.CollectionCount).ToArray();
        var start = Stopwatch.GetTimestamp();
        using var manager = IndexManager.CreateWithDatabasePath(databasePath,
            enforceMeasurementPathSafety: false, layout: SearchStateLayout.Compact);
        manager.OnProgress += progress => {
            if (progress.Phase is { } phase)
                phases.Enqueue(new Phase(phase,
                    Stopwatch.GetElapsedTime(start, progress.ReportedTimestamp).TotalSeconds,
                    progress.ElapsedMs));
            if (progress.Phase == "ready") ready.TrySetResult();
        };
        manager.OnError += errors.Enqueue;
        IndexStats? stats = null;
        string? failure = null;
        try
        {
            await manager.InitializeAsync(roots, ct);
            await ready.Task.WaitAsync(TimeSpan.FromSeconds(10), ct);
            stats = manager.GetStats();
        }
        catch (Exception error)
        {
            failure = error.Message;
        }
        var total = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
        var allocatedBytes = GC.GetTotalAllocatedBytes(precise: true) - allocatedBefore;
        var collections = Enumerable.Range(0, 3)
            .Select(generation => GC.CollectionCount(generation) - collectionsBefore[generation]).ToArray();
        var distinctPhases = phases.DistinctBy(phase => phase.Name).ToArray();
        var catalogMetrics = failure is null && catalogPath is not null ? InspectCatalog(manager, catalogPath) : null;
        var result = new {
            Mode = "exploratory", Source = "filesystem", RootCount = roots.Length,
            DatabasePath = databasePath, Completed = failure is null, Error = failure,
            DiagnosticErrorCount = errors.Count, FileCount = stats?.FileCount,
            DirectoryCount = stats?.DirectoryCount, TokenCount = stats?.TokenCount,
            TotalMilliseconds = total,
            AllocatedBytes = allocatedBytes, GarbageCollections = collections,
            Catalog = catalogMetrics,
            AcquisitionMilliseconds = distinctPhases.FirstOrDefault(phase => phase.Name == "prepare")?.StageElapsedMilliseconds,
            Phases = distinctPhases
        };
        await JsonSerializer.SerializeAsync(outputFile, result, new JsonSerializerOptions { WriteIndented = true }, CancellationToken.None);
        Console.WriteLine(JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true }));
        return failure is null ? 0 : 1;
    }

    private static bool IsWithin(string path, string root) =>
        path.Equals(root, StringComparison.OrdinalIgnoreCase) ||
        path.StartsWith(Path.EndsInDirectorySeparator(root) ? root : root + Path.DirectorySeparatorChar,
            StringComparison.OrdinalIgnoreCase);

    private static object InspectCatalog(IndexManager manager, string path)
    {
        var state = manager.CurrentSearchState;
        GC.Collect();
        GC.WaitForPendingFinalizers();
        var liveCoreBytes = GC.GetTotalMemory(forceFullCollection: true);
        state.GetType().GetMethod("WriteNewBase", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(state, [path]);
        var header = new byte[56];
        using (var stream = File.OpenRead(path)) stream.ReadExactly(header);
        int Field(int offset) => System.Buffers.Binary.BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(offset));
        var queries = new[] { "txt", "pdf", "jpg", "readme", "index", "dart", "flutter", "resim" };
        var measurements = queries.Select(token =>
        {
            _ = state.ForQuery().Get(token).Count;
            var started = Stopwatch.GetTimestamp();
            var count = 0;
            for (var repeat = 0; repeat < 5; repeat++) count = state.ForQuery().Get(token).Count;
            var elapsed = Stopwatch.GetElapsedTime(started).TotalMilliseconds / 5;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            foreach (var item in state.ForQuery().Get(token).OrderBy(item => item.FullPath, StringComparer.Ordinal))
            {
                hash.AppendData(MemoryMarshal.AsBytes(item.FullPath.AsSpan()));
                hash.AppendData(new byte[] { 0, 0 });
            }
            return new { Token = token, Count = count, Milliseconds = elapsed,
                PathsSha256 = Convert.ToHexString(hash.GetHashAndReset()) };
        }).ToArray();
        GC.KeepAlive(manager);
        return new
        {
            LiveCoreManagedBytesAfterForcedGc = liveCoreBytes,
            FormatVersion = Field(4), ItemCount = Field(8), TokenCount = Field(12),
            PayloadBytes = Field(40), BytesPerItem = Field(8) == 0 ? 0 : (double)Field(40) / Field(8),
            UsesVarint = Field(44) == 1,
            Sections = new
            {
                HeaderBytes = Field(16), ItemRowsBytes = Field(20) - Field(16),
                TermRowsBytes = Field(24) - Field(20), ChildrenBytes = Field(28) - Field(24),
                ItemTokensBytes = Field(32) - Field(28), PostingsBytes = Field(36) - Field(32),
                TextBytes = Field(40) - Field(36)
            },
            QuerySamples = measurements
        };
    }

    private sealed record Phase(string Name, double ElapsedSeconds, long StageElapsedMilliseconds);
}
