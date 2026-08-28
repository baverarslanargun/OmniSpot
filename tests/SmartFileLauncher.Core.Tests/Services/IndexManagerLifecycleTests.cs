using System.Collections.Concurrent;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.IO;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexManagerLifecycleTests
{
    [Fact]
    public void CreateWithDatabasePathUsesRequestedPath()
    {
        using var workspace = new TemporaryDirectory();
        var databasePath = Path.Combine(workspace.Path, "measurement-index.db");

        using var manager = IndexManager.CreateWithDatabasePath(databasePath);

        Assert.Equal(databasePath, manager.DatabasePath);
        Assert.False(File.Exists(databasePath));
    }

    [Fact]
    public async Task MeasurementBootstrapSkipsReparsePaths()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var skippedDirectory = workspace.CreateDirectory(Path.Combine("root", "linked"));
        var skippedNestedFile = workspace.CreateFile(
            Path.Combine("root", "linked", "outside-like.txt"));
        var skippedFile = workspace.CreateFile(Path.Combine("root", "linked-file.txt"));
        var includedFile = workspace.CreateFile(Path.Combine("root", "visible.txt"));
        var skippedPaths = new HashSet<string>(
            new[] { skippedDirectory, skippedFile },
            StringComparer.OrdinalIgnoreCase);
        var guard = new FileSystemPathGuard(
            path =>
            {
                var attributes = ReadAttributes(path);
                return attributes.HasValue && skippedPaths.Contains(path)
                    ? attributes.Value | FileAttributes.ReparsePoint
                    : attributes;
            },
            path => Directory.GetFileSystemEntries(path),
            path => path);
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(
            database,
            watcher,
            measurementPathGuard: guard);

        await manager.InitializeAsync(root);

        Assert.NotNull(manager.GetNode(includedFile));
        Assert.Null(manager.GetNode(skippedDirectory));
        Assert.Null(manager.GetNode(skippedNestedFile));
        Assert.Null(manager.GetNode(skippedFile));
        Assert.Equal(1, manager.GetStats().FileCount);
    }

    [Fact]
    public async Task MeasurementBootstrapRejectsReparseRootBeforeWatcherSetup()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var guard = new FileSystemPathGuard(
            path =>
            {
                var attributes = ReadAttributes(path);
                return attributes.HasValue && path.Equals(
                    root,
                    StringComparison.OrdinalIgnoreCase)
                    ? attributes.Value | FileAttributes.ReparsePoint
                    : attributes;
            },
            path => Directory.GetFileSystemEntries(path),
            path => path);
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(
            database,
            watcher,
            measurementPathGuard: guard);

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            manager.InitializeAsync(root));

        Assert.Equal(0, watcher.WatchedPathCount);
        Assert.Null(manager.GetNode(root));
    }

    [Fact]
    public async Task MeasurementModeSkipsRealJunctionAndLoopDuringScanReconcileAndEvents()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var target = workspace.CreateDirectory("outside");
        var link = Path.Combine(root, "linked");
        var loop = Path.Combine(root, "loop");
        var outsideFile = workspace.CreateFile(Path.Combine("outside", "sentinel.txt"));
        var linkedFile = Path.Combine(link, "sentinel.txt");
        var visibleFile = workspace.CreateFile(Path.Combine("root", "visible.txt"));
        WindowsDirectoryLink.CreateJunction(link, target);
        WindowsDirectoryLink.CreateJunction(loop, root);

        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1, skipReparsePoints: true);
        var manager = new IndexManager(
            database,
            watcher,
            reconciliationInterval: TimeSpan.FromMilliseconds(20),
            measurementPathGuard: FileSystemPathGuard.Default,
            skipReparsePoints: true);

        try
        {
            await manager.InitializeAsync(root);

            Assert.NotNull(manager.GetNode(visibleFile));
            Assert.Null(manager.GetNode(outsideFile));
            Assert.Null(manager.GetNode(link));
            Assert.Null(manager.GetNode(loop));

            watcher.TriggerEvent(new FileChangeEvent
            {
                ChangeType = FileChangeType.Created,
                FullPath = linkedFile,
                IsDirectory = false,
                Timestamp = DateTime.UtcNow
            });
            await Task.Delay(100);
            Assert.Null(manager.GetNode(linkedFile));
            Assert.True(SpinWait.SpinUntil(
                () => manager.GetDiagnosticsReport().ReconciliationRuns > 0,
                TimeSpan.FromSeconds(3)));
            Assert.Null(manager.GetNode(linkedFile));
        }
        finally
        {
            manager.Dispose();
            WindowsDirectoryLink.Delete(loop);
            WindowsDirectoryLink.Delete(link);
        }
    }

    [Fact]
    public async Task MultiRootCacheReload_PreservesFilesAndWatcherCoverage()
    {
        using var workspace = new TemporaryDirectory();
        var firstRoot = workspace.CreateDirectory("first-root");
        var secondRoot = workspace.CreateDirectory("second-root");
        var firstFile = workspace.CreateFile(Path.Combine("first-root", "alpha-document.txt"));
        var secondFile = workspace.CreateFile(Path.Combine("second-root", "beta-document.txt"));
        var databasePath = Path.Combine(workspace.Path, "index.db");
        var roots = new[] { firstRoot, secondRoot };

        var firstDatabase = new IndexDatabase(databasePath);
        var firstWatcher = new FileWatcherService(debounceMs: 1);
        var firstManager = new IndexManager(firstDatabase, firstWatcher);

        try
        {
            await firstManager.InitializeAsync(roots);

            Assert.NotNull(firstManager.GetNode(firstFile));
            Assert.NotNull(firstManager.GetNode(secondFile));
            Assert.Equal(2, firstWatcher.WatchedPathCount);
        }
        finally
        {
            firstManager.Dispose();
        }

        var reloadedDatabase = new IndexDatabase(databasePath);
        var reloadedWatcher = new FileWatcherService(debounceMs: 1);
        var reloadedManager = new IndexManager(reloadedDatabase, reloadedWatcher);

        try
        {
            await reloadedManager.InitializeAsync(roots);

            Assert.NotNull(reloadedManager.GetNode(firstFile));
            Assert.NotNull(reloadedManager.GetNode(secondFile));
            Assert.Equal(2, reloadedWatcher.WatchedPathCount);

            var rootNode = Assert.IsType<SmartFileLauncher.Core.Models.FileSystemNode>(
                reloadedManager.RootNode);
            Assert.Contains(rootNode.Children, child =>
                string.Equals(child.FullPath, firstRoot, StringComparison.OrdinalIgnoreCase));
            Assert.Contains(rootNode.Children, child =>
                string.Equals(child.FullPath, secondRoot, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            reloadedManager.Dispose();
        }
    }

    [Fact]
    public async Task InitializeAsync_ReportsIndeterminateSearchPreparation()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "document.txt"));
        using var preparationReported = new ManualResetEventSlim();
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        manager.OnProgress += progress =>
        {
            if (progress.Status == "Arama hazırlanıyor..." && progress.IsIndeterminate)
            {
                preparationReported.Set();
            }
        };

        try
        {
            await manager.InitializeAsync(root);

            Assert.True(preparationReported.Wait(TimeSpan.FromSeconds(3)));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task InitializeTwiceFromTheSameCache_ReplacesRatherThanDuplicatesMemoryState()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var file = workspace.CreateFile(Path.Combine("root", "document.txt"));
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            await manager.InitializeAsync(root);

            Assert.Single(manager.InvertedIndex.Get("document"), node =>
                string.Equals(node.FullPath, file, StringComparison.OrdinalIgnoreCase));
            Assert.Single(manager.MetadataMap, entry =>
                string.Equals(entry.Key, file, StringComparison.OrdinalIgnoreCase));
            Assert.NotNull(manager.GetNode(file));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task ProgressObserver_CanDisposeWhileInitializationOwnsTheLifecycleGate()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "document.txt"));
        using var tokenizer = new BlockingTokenizer();
        using var observerEntered = new ManualResetEventSlim();
        var disposeCompleted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher, tokenizer);
        var notificationCount = 0;

        manager.OnProgress += _ =>
        {
            if (Interlocked.Exchange(ref notificationCount, 1) != 0)
                return;

            observerEntered.Set();
            manager.Dispose();
            disposeCompleted.TrySetResult();
        };

        try
        {
            var initializeTask = Task.Run(() => manager.InitializeAsync(root));

            Assert.True(observerEntered.Wait(TimeSpan.FromSeconds(2)));
            Assert.True(tokenizer.Entered.Wait(TimeSpan.FromSeconds(2)));
            Assert.False(disposeCompleted.Task.IsCompleted);

            tokenizer.Release.Set();
            await initializeTask.WaitAsync(TimeSpan.FromSeconds(3));
            await disposeCompleted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally
        {
            tokenizer.Release.Set();
            manager.Dispose();
        }
    }

    [Fact]
    public async Task GetStats_StaysConsistentWhileFileEventsWriteToTheDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        for (var i = 0; i < 20; i++)
        {
            workspace.CreateFile(Path.Combine("root", $"document-{i}.txt"));
        }

        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            using var start = new ManualResetEventSlim();
            var failures = new ConcurrentQueue<Exception>();
            var reportedErrors = new ConcurrentQueue<string>();
            manager.OnError += message => reportedErrors.Enqueue(message);

            var writer = Task.Run(() =>
            {
                start.Wait();
                try
                {
                    for (var i = 0; i < 40; i++)
                    {
                        var path = workspace.CreateFile(
                            Path.Combine("root", $"stats-live-{i}.txt"));
                        manager.ApplyFileChange(new FileChangeEvent
                        {
                            ChangeType = FileChangeType.Created,
                            FullPath = path,
                            IsDirectory = false
                        });

                        File.Delete(path);
                        manager.ApplyFileChange(new FileChangeEvent
                        {
                            ChangeType = FileChangeType.Deleted,
                            FullPath = path,
                            IsDirectory = false
                        });
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            });

            var readers = Enumerable.Range(0, 3)
                .Select(_ => Task.Run(() =>
                {
                    start.Wait();
                    try
                    {
                        for (var i = 0; i < 60; i++)
                        {
                            var stats = manager.GetStats();
                            Assert.InRange(stats.FileCount, 20, 21);
                            Assert.Equal(1, stats.DirectoryCount);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }))
                .ToArray();

            start.Set();
            await Task.WhenAll(readers.Append(writer))
                .WaitAsync(TimeSpan.FromSeconds(30));
            await manager.QueuedNotifications.WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(
                failures.IsEmpty,
                failures.FirstOrDefault()?.ToString() ?? string.Empty);
            Assert.True(
                reportedErrors.IsEmpty,
                reportedErrors.FirstOrDefault() ?? string.Empty);
            Assert.Equal(20, manager.GetStats().FileCount);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task PublicReadsStayConsistentWhileTheIndexIsReinitialized()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var probe = workspace.CreateFile(Path.Combine("root", "document-0.txt"));
        for (var i = 1; i < 20; i++)
        {
            workspace.CreateFile(Path.Combine("root", $"document-{i}.txt"));
        }

        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);

            var failures = new ConcurrentQueue<Exception>();
            var reads = 0;
            using var stop = new CancellationTokenSource();
            using var readersStarted = new CountdownEvent(3);

            var readers = Enumerable.Range(0, 3)
                .Select(reader => Task.Run(() =>
                {
                    readersStarted.Signal();
                    try
                    {
                        while (!stop.IsCancellationRequested)
                        {
                            var stats = manager.GetStats();
                            Assert.NotNull(stats.DatabasePath);
                            manager.GetNode(probe);
                            _ = manager.RootNode;
                            Interlocked.Increment(ref reads);
                        }
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                    }
                }))
                .ToArray();

            Assert.True(readersStarted.Wait(TimeSpan.FromSeconds(10)));

            for (var i = 0; i < 5; i++)
            {
                await manager.InitializeAsync(root);
            }

            stop.Cancel();
            await Task.WhenAll(readers).WaitAsync(TimeSpan.FromSeconds(30));

            Assert.True(
                failures.IsEmpty,
                failures.FirstOrDefault()?.ToString() ?? string.Empty);
            Assert.True(Volatile.Read(ref reads) > 0);
            Assert.NotNull(manager.GetNode(probe));
            Assert.Equal(20, manager.GetStats().FileCount);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task EnsureSyncedAsyncYieldsWhileInitializationHoldsTheLifecycleGate()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "document.txt"));
        using var tokenizer = new BlockingTokenizer();
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher, tokenizer);

        try
        {
            var initializeTask = Task.Run(() => manager.InitializeAsync(root));
            Assert.True(tokenizer.Entered.Wait(TimeSpan.FromSeconds(3)));

            Assert.False(
                await manager.EnsureSyncedAsync(root)
                    .WaitAsync(TimeSpan.FromSeconds(3)));

            tokenizer.Release.Set();
            await initializeTask.WaitAsync(TimeSpan.FromSeconds(10));

            Assert.True(
                await manager.EnsureSyncedAsync(root)
                    .WaitAsync(TimeSpan.FromSeconds(10)));
        }
        finally
        {
            tokenizer.Release.Set();
            manager.Dispose();
        }
    }

    [Fact]
    public async Task EnsureSyncedAsyncStaysConsistentAcrossReinitialize()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        for (var i = 0; i < 20; i++)
        {
            workspace.CreateFile(Path.Combine("root", $"document-{i}.txt"));
        }

        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            var failures = new ConcurrentQueue<Exception>();
            var syncs = 0;
            using var stop = new CancellationTokenSource();
            using var syncerStarted = new CountdownEvent(1);

            var syncer = Task.Run(async () =>
            {
                syncerStarted.Signal();
                try
                {
                    while (!stop.IsCancellationRequested)
                    {
                        await manager.EnsureSyncedAsync(root);
                        Interlocked.Increment(ref syncs);
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
            });

            Assert.True(syncerStarted.Wait(TimeSpan.FromSeconds(10)));

            for (var i = 0; i < 5; i++)
            {
                workspace.CreateFile(Path.Combine("root", $"late-{i}.txt"));
                await manager.InitializeAsync(root);
            }

            stop.Cancel();
            await syncer.WaitAsync(TimeSpan.FromSeconds(30));
            await manager.EnsureSyncedAsync(root);

            Assert.True(
                failures.IsEmpty,
                failures.FirstOrDefault()?.ToString() ?? string.Empty);
            Assert.True(Volatile.Read(ref syncs) > 0);

            var expected = Directory.GetFiles(root).Length;
            Assert.Equal(expected, manager.GetStats().FileCount);
            Assert.Equal(
                expected,
                manager.IndexedEntries.Count(entry => !entry.Value.IsDirectory));
        }
        finally
        {
            manager.Dispose();
        }
    }

    private sealed class BlockingTokenizer : ITokenizer, IDisposable
    {
        private readonly BasicTokenizer _inner = new();

        public ManualResetEventSlim Entered { get; } = new();
        public ManualResetEventSlim Release { get; } = new();

        public IEnumerable<string> Tokenize(string input)
        {
            Entered.Set();
            Release.Wait(TimeSpan.FromSeconds(5));
            return _inner.Tokenize(input);
        }

        public void Dispose()
        {
            Entered.Dispose();
            Release.Dispose();
        }
    }

    private static FileAttributes? ReadAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
    }
}
