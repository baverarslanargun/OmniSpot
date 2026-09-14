using SmartFileLauncher.Core.Indexing;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexManagerInventoryTests
{
    [Fact]
    public async Task BootstrapUsesInventoryMetadataAndPublishesBeforeValidationHook()
    {
        using var world = new World();
        var path = world.Workspace.CreateFile(@"root\file.txt", "x");
        var source = new Source((receive, _) =>
        {
            Assert.True(world.Watcher.IsWatching);
            receive(Entry(world.Root, true));
            receive(Entry(path) with { SizeBytes = 777 });
            return Task.CompletedTask;
        });
        await world.Initialize(source, async () =>
        {
            Assert.Equal(777, world.Database.GetFileByPath(path)!.SizeBytes);
            Assert.True(await world.Manager.ValidateInitialInventoryAsync(default));
        });
        Assert.Equal(777, world.Database.GetFileByPath(path)!.SizeBytes);
        Assert.Equal("0", world.Database.GetMetadata(IndexMetadata.Keys.InitialInventoryPending));
        Assert.True(source.Session.Disposed);
    }

    [Fact]
    public async Task APartialInventoryIsDiscardedAndNormalScanCompletes()
    {
        using var world = new World();
        var real = world.Workspace.CreateFile(@"root\real.txt", "abc");
        var phantom = Path.Combine(world.Root, "phantom.txt");
        var source = new Source((receive, _) =>
        {
            receive(Entry(world.Root, true));
            receive(Entry(phantom));
            return Task.CompletedTask;
        }) { ReturnSession = false };
        await world.Initialize(source);
        Assert.NotNull(world.Database.GetFileByPath(real));
        Assert.Null(world.Database.GetFileByPath(phantom));
    }

    [Fact]
    public async Task ADisappearedReparseEntryDoesNotDiscardTheRestOfTheInventory()
    {
        using var world = new World();
        var real = world.Workspace.CreateFile(@"root\real.txt", "abc");
        var phantom = Path.Combine(world.Root, "link.txt");
        var source = new Source((receive, _) =>
        {
            receive(Entry(world.Root, true));
            receive(Entry(real) with { SizeBytes = 777 });
            receive(Entry(phantom) with { Attributes = FileAttributes.ReparsePoint });
            return Task.CompletedTask;
        });
        await world.Initialize(source);
        Assert.Equal(777, world.Database.GetFileByPath(real)!.SizeBytes);
        Assert.Null(world.Database.GetFileByPath(phantom));
        Assert.Equal("0", world.Database.GetMetadata(IndexMetadata.Keys.InitialInventoryPending));
        Assert.Equal("mft", world.Database.GetMetadata(IndexMetadata.Keys.LastBootstrapSource));
    }

    [Fact]
    public async Task OnlyJunctionContentsAreTraversedAndChangesBeforeHandoffAreReconciled()
    {
        using var world = new World();
        var target = world.Workspace.CreateDirectory("target");
        var before = world.Workspace.CreateFile(@"target\before.txt", "before");
        var ordinary = world.Workspace.CreateFile(@"root\ordinary.txt", "x");
        var link = Path.Combine(world.Root, "link");
        WindowsDirectoryLink.CreateJunction(link, target);
        try
        {
            var source = new Source((receive, _) =>
            {
                receive(Entry(world.Root, true));
                receive(Entry(ordinary) with { SizeBytes = 777 });
                receive(Entry(link, true) with { Attributes = FileAttributes.Directory | FileAttributes.ReparsePoint });
                return Task.CompletedTask;
            });
            await world.Initialize(source, async () =>
            {
                Assert.NotNull(world.Database.GetFileByPath(Path.Combine(link, "before.txt")));
                File.Delete(before);
                File.WriteAllText(Path.Combine(target, "after.txt"), "after");
                Assert.True(await world.Manager.ValidateInitialInventoryAsync(default));
                Assert.Null(world.Database.GetFileByPath(Path.Combine(link, "before.txt")));
                Assert.Equal(5, world.Database.GetFileByPath(Path.Combine(link, "after.txt"))!.SizeBytes);
            });
            Assert.Equal(777, world.Database.GetFileByPath(ordinary)!.SizeBytes);
            Assert.Equal("mft", world.Database.GetMetadata(IndexMetadata.Keys.LastBootstrapSource));
            Assert.Equal("1", world.Database.GetMetadata(IndexMetadata.Keys.LastBootstrapLinkScopes));
            Assert.Equal("0", world.Database.GetMetadata(IndexMetadata.Keys.InitialInventoryPending));
        }
        finally { WindowsDirectoryLink.Delete(link); }
    }

    [Fact]
    public async Task ChangesDuringInventoryAreDispatchedAfterTheBaseline()
    {
        using var world = new World();
        var deleted = world.Workspace.CreateFile(@"root\deleted.txt", "old");
        var renamed = world.Workspace.CreateFile(@"root\before.txt", "renamed");
        var moved = Path.Combine(world.Root, "after.txt");
        var created = Path.Combine(world.Root, "created.txt");
        var source = new Source((receive, _) =>
        {
            receive(Entry(world.Root, true));
            receive(Entry(deleted));
            receive(Entry(renamed));
            File.Delete(deleted);
            File.Move(renamed, moved);
            File.WriteAllText(created, "new");
            world.Watcher.TriggerEvent(new FileChangeEvent { FullPath = deleted, ChangeType = FileChangeType.Deleted });
            world.Watcher.TriggerEvent(new FileChangeEvent { FullPath = moved, OldPath = renamed, ChangeType = FileChangeType.Renamed });
            world.Watcher.TriggerEvent(new FileChangeEvent { FullPath = created, ChangeType = FileChangeType.Created });
            return Task.CompletedTask;
        });
        await world.Initialize(source);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (world.Manager.CurrentSearchState.ContainsPath(deleted) ||
               world.Manager.CurrentSearchState.ContainsPath(renamed) ||
               !world.Manager.CurrentSearchState.ContainsPath(moved) ||
               !world.Manager.CurrentSearchState.ContainsPath(created))
            await Task.Delay(10, deadline.Token);
    }

    [Fact]
    public async Task AWatcherOverflowInvalidatesTheInventoryAndFallsBack()
    {
        using var world = new World();
        var real = world.Workspace.CreateFile(@"root\real.txt", "abc");
        var phantom = Path.Combine(world.Root, "phantom.txt");
        var source = new Source((receive, _) =>
        {
            receive(Entry(world.Root, true));
            receive(Entry(phantom));
            world.Watcher.TriggerError(new InternalBufferOverflowException());
            return Task.CompletedTask;
        });
        await world.Initialize(source, async () =>
            Assert.False(await world.Manager.ValidateInitialInventoryAsync(default)));
        Assert.NotNull(world.Database.GetFileByPath(real));
        Assert.Null(world.Database.GetFileByPath(phantom));
    }

    [Fact]
    public async Task CancelledInventoryLeavesNoPausedWatcherOrPartialIndex()
    {
        using var world = new World();
        using var cancellation = new CancellationTokenSource();
        var source = new Source((receive, ct) =>
        {
            receive(Entry(world.Root, true));
            cancellation.Cancel();
            ct.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        });
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            world.Manager.InitializeWithWatcherFenceAsync([world.Root], cancellation.Token, null, source));
        Assert.False(world.Watcher.IsWatching);
        Assert.False(world.Manager.IsInitialized);
        Assert.Equal(0, world.Database.GetFileCount());
    }

    [Fact]
    public async Task CachedStartupDoesNotReadInventoryAgain()
    {
        using var world = new World();
        var path = world.Workspace.CreateFile(@"root\file.txt", "abc");
        var source = new Source((receive, _) =>
        {
            receive(Entry(world.Root, true));
            receive(Entry(path));
            return Task.CompletedTask;
        });
        await world.Initialize(source);
        await world.Initialize(source);
        Assert.Equal(1, source.Reads);
    }

    [Fact]
    public async Task UnfinishedInventoryInTheDatabaseForcesANewBaseline()
    {
        using var world = new World();
        var path = world.Workspace.CreateFile(@"root\file.txt", "abc");
        var source = new Source((receive, _) =>
        {
            receive(Entry(world.Root, true));
            receive(Entry(path));
            return Task.CompletedTask;
        });
        await world.Initialize(source, () =>
        {
            Assert.Equal("1", world.Database.GetMetadata(IndexMetadata.Keys.InitialInventoryPending));
            return Task.CompletedTask;
        });
        world.Database.SetMetadata(IndexMetadata.Keys.InitialInventoryPending, "1");
        await world.Initialize(source);
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task InitialCaptureIncludesNormallyExcludedPathsUntilTheDrain()
    {
        using var world = new World();
        var created = Path.Combine(world.Root, "created.tmp");
        var source = new Source((receive, _) =>
        {
            receive(Entry(world.Root, true));
            File.WriteAllText(created, "new");
            world.Watcher.OnFileCreated(world.Watcher,
                new FileSystemEventArgs(WatcherChangeTypes.Created, world.Root, "created.tmp"));
            return Task.CompletedTask;
        });
        await world.Initialize(source);
        Assert.NotNull(world.Database.GetFileByPath(created));
        Assert.Equal("0", world.Database.GetMetadata(IndexMetadata.Keys.InitialInventoryPending));
        var ignored = Path.Combine(world.Root, "later.tmp");
        File.WriteAllText(ignored, "later");
        world.Watcher.OnFileCreated(world.Watcher,
            new FileSystemEventArgs(WatcherChangeTypes.Created, world.Root, "later.tmp"));
        await world.Watcher.DrainAsync(default);
        Assert.Null(world.Database.GetFileByPath(ignored));
    }

    [Fact]
    public async Task AnErrorDuringTheFinalDrainDropsCoverageAndReconciles()
    {
        using var world = new World();
        var missed = world.Workspace.CreateFile(@"root\missed.txt", "new");
        var trigger = world.Workspace.CreateFile(@"root\trigger.txt", "trigger");
        var source = new Source((receive, _) =>
        {
            receive(Entry(world.Root, true));
            world.Watcher.TriggerEvent(new FileChangeEvent
            {
                FullPath = trigger, ChangeType = FileChangeType.Created
            });
            return Task.CompletedTask;
        });
        world.Watcher.OnChange += _ => world.Watcher.TriggerError(new InternalBufferOverflowException());
        await world.Initialize(source);
        Assert.False(world.Manager.ChangeFeedGuarding);
        Assert.False(world.Manager.ChangeFeedCoversDowntime);
        Assert.NotNull(world.Database.GetFileByPath(missed));
        Assert.Equal("1", world.Database.GetMetadata(IndexMetadata.Keys.InitialInventoryPending));
    }

    private static IndexInventoryEntry Entry(string path, bool directory = false) =>
        new(path, directory, directory ? FileAttributes.Directory : FileAttributes.Normal,
            directory ? 0 : 3, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks);

    private sealed class Source(Func<Action<IndexInventoryEntry>, CancellationToken, Task> read) : IIndexInventorySource
    {
        public Session Session { get; } = new();
        public bool ReturnSession { get; init; } = true;
        public int Reads { get; private set; }
        public async Task<IIndexInventorySession?> ReadAsync(IReadOnlyList<string> roots,
            Action<IndexInventoryEntry> receive, CancellationToken cancellationToken)
        {
            Reads++;
            await read(receive, cancellationToken);
            return ReturnSession ? Session : null;
        }
    }

    private sealed class Session : IIndexInventorySession
    {
        public bool Disposed { get; private set; }
        public Task<bool> ValidateAsync(CancellationToken ct) => Task.FromResult(true);
        public ValueTask DisposeAsync() { Disposed = true; return ValueTask.CompletedTask; }
    }

    private sealed class World : IDisposable
    {
        public TemporaryDirectory Workspace { get; } = new();
        public string Root { get; }
        public IndexDatabase Database { get; }
        public FileWatcherService Watcher { get; } = new(debounceMs: 1);
        public IndexManager Manager { get; }
        public World()
        {
            Root = Workspace.CreateDirectory("root");
            Database = new IndexDatabase(Path.Combine(Workspace.Path, "index.db"));
            Manager = new IndexManager(Database, Watcher, reconciliationInterval: TimeSpan.FromHours(1),
                layout: SearchStateLayout.Compact);
        }
        public Task Initialize(IIndexInventorySource source, Func<Task>? beforeResume = null) =>
            Manager.InitializeWithWatcherFenceAsync([Root], default, async (roots, _) =>
            {
                if (beforeResume is not null) await beforeResume();
                Manager.NoteChangeFeedCoverage(true);
                return Manager.BeginWatcherCaptureWithinLifecycle(roots);
            }, source);
        public void Dispose() { Manager.Dispose(); Workspace.Dispose(); }
    }
}
