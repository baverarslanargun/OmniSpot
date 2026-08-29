using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.UI.Services;

namespace SmartFileLauncher.UI.Tests.Services;

internal sealed class FakeIndexLifecycle : IIndexLifecycleService
{
    public Exception? StatsFailure { get; set; }

    public event Action<IndexProgress>? ProgressChanged { add { } remove { } }
    public event Action<FileChangeEvent>? FileChanged { add { } remove { } }
    public event Action<string>? Error { add { } remove { } }
    public event Action<int, int, int>? ReconciliationProgressChanged { add { } remove { } }
    public event Action<bool>? ReconciliationStateChanged { add { } remove { } }

    public bool IsInitialized => true;
    public string DatabasePath => @"C:\test\index.db";
    public IndexReconciliationStatus ReconciliationStatus { get; } =
        new(IsRunning: false, Progress: 0, Processed: 0, Total: 0);

    public IndexStats GetStats()
    {
        if (StatsFailure is not null) throw StatsFailure;
        return new IndexStats
        {
            FileCount = 12,
            DirectoryCount = 3,
            TokenCount = 40,
            DatabasePath = DatabasePath,
            LastScanTime = new DateTime(2026, 8, 28, 1, 25, 0, DateTimeKind.Local)
        };
    }

    public IndexDiagnosticsReport GetDiagnosticsReport() => new(
        ReconciliationRuns: 1,
        LastReconciliationAt: new DateTime(2026, 8, 28, 1, 27, 0, DateTimeKind.Local),
        LastReconciliationDuration: TimeSpan.FromSeconds(25),
        LastReconciliationScanDuration: TimeSpan.FromSeconds(20),
        LastReconciliationChanges: 0,
        RepublishedDuringLastReconciliation: false,
        RepublishCount: 0,
        LastRepublishAt: null,
        LastRepublishDuration: TimeSpan.Zero,
        SearchStateItemCount: 15);

    public Task<IndexStartupResult> InitializeAsync(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public Task<bool> EnsureSyncedAsync(string path, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IReadOnlyList<FileSystemNode> GetIndexedRoots(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public IndexTokenMatches GetTokenMatches(string token, CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public SearchState CreateSearchState(CancellationToken cancellationToken = default)
        => throw new NotSupportedException();
    public void RecordOpened(string path) => throw new NotSupportedException();
    public void Dispose() { }
}

internal sealed class FakeThumbnailService : IThumbnailService
{
    public Task<System.Windows.Media.ImageSource?> GetThumbnailAsync(
        string path, int size, CancellationToken token = default)
        => throw new NotSupportedException();

    public ThumbnailDiagnostics GetDiagnostics() => new(
        MemoryCacheCount: 0,
        MemoryCacheLimit: 1000,
        MemoryCacheByteLimit: 64L * 1024 * 1024,
        Requests: 0,
        MemoryHits: 0,
        DiskHits: 0,
        ShellGenerated: 0,
        Failures: 0,
        LastDecodedPixelWidth: 0,
        LastDecodedPixelHeight: 0,
        DecodedBytes: 0,
        ActiveGenerations: 0,
        QueuedGenerations: 0,
        Evictions: 0,
        DiskCacheFileCount: 0,
        DiskCacheBytes: 0,
        DiskCacheMeasuredAt: null);

    public Task RefreshDiskCacheStatsAsync(CancellationToken token = default)
        => Task.CompletedTask;
}
