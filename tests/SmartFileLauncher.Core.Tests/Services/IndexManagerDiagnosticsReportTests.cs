using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexManagerDiagnosticsReportTests
{
    [Fact]
    public void NewManagerReportsNoReconciliationAndNoRepublish()
    {
        using var workspace = new TemporaryDirectory();
        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(database, watcher);

        var report = manager.GetDiagnosticsReport();

        Assert.Equal(0, report.ReconciliationRuns);
        Assert.Null(report.LastReconciliationAt);
        Assert.Equal(0, report.RepublishCount);
        Assert.Null(report.LastRepublishAt);
        Assert.Equal(0, report.SearchStateItemCount);
    }

    [Fact]
    public async Task InitializationReportsReconciliationRunAndTiming()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "document.txt"));

        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(database, watcher);

        await manager.InitializeAsync(root);
        var report = await WaitForReconciliationAsync(manager);

        Assert.True(report.ReconciliationRuns >= 1);
        Assert.NotNull(report.LastReconciliationAt);
        Assert.True(report.LastReconciliationDuration > TimeSpan.Zero);
        Assert.True(report.LastReconciliationScanDuration > TimeSpan.Zero);
        Assert.True(
            report.LastReconciliationScanDuration <= report.LastReconciliationDuration);
    }

    [Fact]
    public async Task InitializationReportsPublishedSearchStateItemCount()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "first.txt"));
        workspace.CreateFile(Path.Combine("root", "second.txt"));

        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(database, watcher);

        await manager.InitializeAsync(root);
        await WaitForReconciliationAsync(manager);

        var report = manager.GetDiagnosticsReport();

        Assert.Equal(manager.CurrentSearchState.ItemCount, report.SearchStateItemCount);
        Assert.True(report.SearchStateItemCount >= 2);
    }

    [Fact]
    public async Task RepublishIsCountedAndTimed()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "document.txt"));

        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(database, watcher);

        await manager.InitializeAsync(root);
        await WaitForReconciliationAsync(manager);

        var report = manager.GetDiagnosticsReport();

        Assert.True(report.RepublishCount >= 1);
        Assert.NotNull(report.LastRepublishAt);
    }

    [Fact]
    public async Task StartupRepublishIsNotAttributedToReconciliation()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "document.txt"));

        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(database, watcher);

        await manager.InitializeAsync(root);
        var report = await WaitForReconciliationAsync(manager);

        Assert.Equal(0, report.LastReconciliationChanges);
        Assert.True(report.RepublishCount >= 1);
        Assert.False(report.RepublishedDuringLastReconciliation);
    }

    [Fact]
    public async Task RepublishFlagResetsOnTheNextReconciliation()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "kept.txt"));
        var databasePath = Path.Combine(workspace.Path, "index.db");

        using (var firstDatabase = new IndexDatabase(databasePath))
        using (var firstWatcher = new FileWatcherService(debounceMs: 1))
        using (var firstManager = new IndexManager(firstDatabase, firstWatcher))
        {
            await firstManager.InitializeAsync(root);
            await WaitForReconciliationAsync(firstManager);
        }

        workspace.CreateFile(Path.Combine("root", "added-while-closed.txt"));

        using var database = new IndexDatabase(databasePath);
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(
            database,
            watcher,
            reconciliationInterval: TimeSpan.FromSeconds(1));

        await manager.InitializeAsync(root);
        var afterChanges = await WaitForReconciliationAsync(manager);
        Assert.True(afterChanges.RepublishedDuringLastReconciliation);

        var afterQuietRun = await WaitForReconciliationAsync(manager, minimumRuns: 2);

        Assert.Equal(0, afterQuietRun.LastReconciliationChanges);
        Assert.False(afterQuietRun.RepublishedDuringLastReconciliation);
    }

    [Fact]
    public async Task ReconciliationWithChangesRepublishesWithinTheRun()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "kept.txt"));
        var databasePath = Path.Combine(workspace.Path, "index.db");

        using (var firstDatabase = new IndexDatabase(databasePath))
        using (var firstWatcher = new FileWatcherService(debounceMs: 1))
        using (var firstManager = new IndexManager(firstDatabase, firstWatcher))
        {
            await firstManager.InitializeAsync(root);
            await WaitForReconciliationAsync(firstManager);
        }

        workspace.CreateFile(Path.Combine("root", "added-while-closed.txt"));

        using var database = new IndexDatabase(databasePath);
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(database, watcher);

        await manager.InitializeAsync(root);
        var report = await WaitForReconciliationAsync(manager);

        Assert.True(report.RepublishedDuringLastReconciliation);
    }

    [Fact]
    public async Task CacheReloadWithDiskChangesReportsChangeCount()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "kept.txt"));
        var databasePath = Path.Combine(workspace.Path, "index.db");

        using (var firstDatabase = new IndexDatabase(databasePath))
        using (var firstWatcher = new FileWatcherService(debounceMs: 1))
        using (var firstManager = new IndexManager(firstDatabase, firstWatcher))
        {
            await firstManager.InitializeAsync(root);
            await WaitForReconciliationAsync(firstManager);
        }

        workspace.CreateFile(Path.Combine("root", "added-while-closed.txt"));

        using var database = new IndexDatabase(databasePath);
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(database, watcher);

        await manager.InitializeAsync(root);
        var report = await WaitForReconciliationAsync(manager);

        Assert.True(report.LastReconciliationChanges > 0);
    }

    private static async Task<IndexDiagnosticsReport> WaitForReconciliationAsync(
        IndexManager manager,
        int minimumRuns = 1)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (DateTime.UtcNow < deadline)
        {
            var report = manager.GetDiagnosticsReport();
            if (report.ReconciliationRuns >= minimumRuns && !manager.IsDeltaSyncRunning)
                return report;

            await Task.Delay(25);
        }

        return manager.GetDiagnosticsReport();
    }
}
