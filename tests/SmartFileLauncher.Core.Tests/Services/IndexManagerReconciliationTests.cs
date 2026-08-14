using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexManagerReconciliationTests
{
    [Fact]
    public async Task BackgroundReconciliation_ReportsRunningAndCompletedStates()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "document.txt"));
        using var reconciliationCompleted = new ManualResetEventSlim();
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);
        var stateLock = new object();
        var states = new List<bool>();

        manager.OnDeltaSyncStateChanged += isRunning =>
        {
            lock (stateLock)
            {
                states.Add(isRunning);
            }

            if (!isRunning)
            {
                reconciliationCompleted.Set();
            }
        };

        try
        {
            await manager.InitializeAsync(root);

            Assert.True(reconciliationCompleted.Wait(TimeSpan.FromSeconds(3)));
            bool[] observedStates;
            lock (stateLock)
            {
                observedStates = states.ToArray();
            }

            Assert.True(observedStates[0]);
            Assert.False(observedStates[^1]);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task CacheReload_ReconcilesRecursiveCreateDeleteAndRename()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var renamedSource = workspace.CreateDirectory(Path.Combine("root", "rename-me"));
        workspace.CreateFile(Path.Combine("root", "rename-me", "child", "old.txt"));
        var deletedDirectory = workspace.CreateDirectory(Path.Combine("root", "delete-me"));
        workspace.CreateFile(Path.Combine("root", "delete-me", "ghost.txt"));
        workspace.CreateFile(Path.Combine("root", "keep.txt"));

        var databasePath = Path.Combine(workspace.Path, "index.db");
        using (var firstDatabase = new IndexDatabase(databasePath))
        using (var firstWatcher = new FileWatcherService(debounceMs: 1))
        using (var firstManager = new IndexManager(firstDatabase, firstWatcher))
        {
            await firstManager.InitializeAsync(root);
            await WaitForReconciliationAsync(firstManager, minimumRunCount: 1);
        }

        var renamedDestination = Path.Combine(root, "renamed");
        Directory.Move(renamedSource, renamedDestination);
        Directory.Delete(deletedDirectory, recursive: true);
        workspace.CreateFile(Path.Combine("root", "new", "deep", "created.txt"));

        var reloadedDatabase = new IndexDatabase(databasePath);
        var reloadedWatcher = new FileWatcherService(debounceMs: 1);
        var reloadedManager = new IndexManager(reloadedDatabase, reloadedWatcher);

        try
        {
            await reloadedManager.InitializeAsync(root);
            await WaitForReconciliationAsync(reloadedManager, minimumRunCount: 1);

            Assert.Null(reloadedManager.GetNode(renamedSource));
            Assert.Null(reloadedManager.GetNode(deletedDirectory));
            Assert.NotNull(reloadedManager.GetNode(renamedDestination));
            Assert.NotNull(reloadedManager.GetNode(
                Path.Combine(renamedDestination, "child", "old.txt")));
            AssertIndexMatchesDisk(root, reloadedManager, reloadedDatabase);
        }
        finally
        {
            reloadedManager.Dispose();
        }
    }

    [Fact]
    public async Task EnsureSynced_RecursivelyRepairsMissedChangesAndMetadata()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var deletedDirectory = workspace.CreateDirectory(Path.Combine("root", "delete-me"));
        workspace.CreateFile(Path.Combine("root", "delete-me", "ghost.txt"));
        var modifiedFile = workspace.CreateFile(Path.Combine("root", "modified.txt"), "old");
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            await WaitForReconciliationAsync(manager, minimumRunCount: 1);
            watcher.Stop();

            Directory.Delete(deletedDirectory, recursive: true);
            var createdFile = workspace.CreateFile(
                Path.Combine("root", "new", "deep", "created.txt"),
                "new");
            File.WriteAllText(modifiedFile, "updated contents");
            File.SetLastWriteTimeUtc(modifiedFile, DateTime.UtcNow.AddSeconds(2));

            Assert.True(await manager.EnsureSyncedAsync(root));

            Assert.Null(manager.GetNode(deletedDirectory));
            Assert.NotNull(manager.GetNode(createdFile));
            Assert.Equal(
                new FileInfo(modifiedFile).Length,
                manager.MetadataMap[modifiedFile].SizeBytes);
            AssertIndexMatchesDisk(root, manager, database);

            database.DeleteDirectory(Path.Combine(root, "new"));
            Assert.Null(database.GetFileByPath(createdFile));

            Assert.True(await manager.EnsureSyncedAsync(root));
            AssertIndexMatchesDisk(root, manager, database);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task WatcherOverflow_TriggersTheSharedReconciliationPath()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "seed.txt"));
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            await WaitForReconciliationAsync(manager, minimumRunCount: 1);
            watcher.Stop();

            var createdFile = workspace.CreateFile(
                Path.Combine("root", "missed", "after-overflow.txt"));
            var previousRunCount = manager.ReconciliationRunCount;

            watcher.TriggerError(new InternalBufferOverflowException("test overflow"));
            await WaitForReconciliationAsync(
                manager,
                minimumRunCount: previousRunCount + 1);

            Assert.NotNull(manager.GetNode(createdFile));
            AssertIndexMatchesDisk(root, manager, database);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task PeriodicReconciliation_RepairsABatchOfMissedTreeChanges()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        for (var index = 0; index < 30; index++)
        {
            workspace.CreateFile(Path.Combine(
                "root",
                $"batch-{index:D2}",
                "nested",
                $"file-{index:D2}.txt"));
        }

        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(
            database,
            watcher,
            reconciliationInterval: TimeSpan.FromSeconds(1));

        try
        {
            await manager.InitializeAsync(root);
            await WaitForReconciliationAsync(manager, minimumRunCount: 1);
            watcher.Stop();

            for (var index = 0; index < 10; index++)
            {
                Directory.Delete(
                    Path.Combine(root, $"batch-{index:D2}"),
                    recursive: true);
            }

            for (var index = 10; index < 20; index++)
            {
                Directory.Move(
                    Path.Combine(root, $"batch-{index:D2}"),
                    Path.Combine(root, $"renamed-{index:D2}"));
            }

            for (var index = 30; index < 40; index++)
            {
                workspace.CreateFile(Path.Combine(
                    "root",
                    $"created-{index:D2}",
                    "nested",
                    $"file-{index:D2}.txt"));
            }

            var previousRunCount = manager.ReconciliationRunCount;
            await WaitForReconciliationAsync(
                manager,
                minimumRunCount: previousRunCount + 2,
                timeout: TimeSpan.FromSeconds(5));

            AssertIndexMatchesDisk(root, manager, database);
        }
        finally
        {
            manager.Dispose();
        }
    }

    private static async Task WaitForReconciliationAsync(
        IndexManager manager,
        long minimumRunCount,
        TimeSpan? timeout = null)
    {
        var deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(3));
        while (DateTime.UtcNow < deadline)
        {
            if (manager.ReconciliationRunCount >= minimumRunCount &&
                !manager.IsDeltaSyncRunning)
            {
                return;
            }

            await Task.Delay(20);
        }

        Assert.Fail(
            $"Reconciliation did not reach run {minimumRunCount}. " +
            $"Current run: {manager.ReconciliationRunCount}.");
    }

    private static void AssertIndexMatchesDisk(
        string root,
        IndexManager manager,
        IndexDatabase database)
    {
        var expectedDirectories = Directory
            .EnumerateDirectories(root, "*", SearchOption.AllDirectories)
            .Append(root)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedFiles = Directory
            .EnumerateFiles(root, "*", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var persistedDirectories = database.GetAllDirectories()
            .Select(directory => directory.FullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var persistedFiles = database.GetAllFiles()
            .Select(file => file.FullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedDirectories, persistedDirectories);
        Assert.Equal(expectedFiles, persistedFiles);
        Assert.Equal(expectedFiles, manager.MetadataMap.Keys
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase));

        var rootNode = Assert.IsType<FileSystemNode>(manager.RootNode);
        var indexedPaths = EnumerateTree(rootNode)
            .Select(node => node.FullPath)
            .Where(path => !string.IsNullOrEmpty(path))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();
        var expectedPaths = expectedDirectories
            .Concat(expectedFiles)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expectedPaths, indexedPaths);
        Assert.All(expectedPaths, path => Assert.NotNull(manager.GetNode(path)));
    }

    private static IEnumerable<FileSystemNode> EnumerateTree(FileSystemNode root)
    {
        var pending = new Stack<FileSystemNode>();
        pending.Push(root);

        while (pending.Count > 0)
        {
            var node = pending.Pop();
            yield return node;

            foreach (var child in node.Children)
            {
                pending.Push(child);
            }
        }
    }
}
