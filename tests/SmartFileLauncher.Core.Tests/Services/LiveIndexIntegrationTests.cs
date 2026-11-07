using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Indexing;
using SmartFileLauncher.Core.Indexing.Ntfs;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class LiveIndexIntegrationTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    [System.Runtime.Versioning.SupportedOSPlatform("windows")]
    public async Task UnwatchableJunctionDoesNotAbortMftInventoryOrDisableOtherLinks(bool blockedFirst)
    {
        using var space = new TemporaryDirectory();
        var root = space.CreateDirectory("files");
        var denied = space.CreateDirectory("denied");
        var readable = space.CreateDirectory("readable");
        space.CreateFile(@"denied\private.txt", "private");
        space.CreateFile(@"readable\visible.txt", "visible");
        var blockedLink = Path.Combine(root, "blocked");
        var healthyLink = Path.Combine(root, "healthy");
        WindowsDirectoryLink.CreateJunction(blockedLink, denied);
        WindowsDirectoryLink.CreateJunction(healthyLink, readable);
        var target = new DirectoryInfo(denied);
        var access = target.GetAccessControl();
        var originalAccess = access.GetSecurityDescriptorBinaryForm();
        using var identity = WindowsIdentity.GetCurrent();
        try
        {
            access.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            target.SetAccessControl(access);
            using var manager = Create(Path.Combine(space.Path, "index.db"));
            var source = new LinkInventory(blockedFirst ? [blockedLink, healthyLink] : [healthyLink, blockedLink]);
            await manager.InitializeLiveAsync([root], source, new(new Channel(), new IndexManagerChangeFeedTarget(manager)), CancellationToken.None);
            Assert.True(manager.IsInitialized);
            Assert.True(manager.CurrentSearchState.ContainsPath(blockedLink));
            Assert.False(manager.CurrentSearchState.ContainsPath(Path.Combine(blockedLink, "private.txt")));
            Assert.True(manager.CurrentSearchState.ContainsPath(Path.Combine(healthyLink, "visible.txt")));
            Assert.DoesNotContain(blockedLink, manager.LiveWatcherRoots);
            Assert.Contains(healthyLink, manager.LiveWatcherRoots);
            Assert.True(manager.IsWatching);
        }
        finally
        {
            var restore = target.GetAccessControl();
            restore.RemoveAccessRuleAll(new FileSystemAccessRule(identity.User!, FileSystemRights.ListDirectory, AccessControlType.Deny));
            restore.SetSecurityDescriptorBinaryForm(originalAccess, AccessControlSections.Access);
            target.SetAccessControl(restore);
            WindowsDirectoryLink.Delete(blockedLink);
            WindowsDirectoryLink.Delete(healthyLink);
        }
    }

    [Fact]
    public async Task HiddenAndSystemSubtreesRemainVisibleThroughLiveUpdates()
    {
        using var space = new TemporaryDirectory();
        var root = space.CreateDirectory("files");
        var folder = space.CreateDirectory(@"files\hidden");
        var file = space.CreateFile(@"files\hidden\system.txt", "initial");
        File.SetAttributes(folder, FileAttributes.Directory | FileAttributes.Hidden);
        File.SetAttributes(file, FileAttributes.System);
        using var manager = Create(Path.Combine(space.Path, "index.db"));
        await manager.InitializeLiveAsync([root], null, new(new Channel(), new IndexManagerChangeFeedTarget(manager)), CancellationToken.None);
        Assert.True(manager.CurrentSearchState.ContainsPath(file));
        File.AppendAllText(file, " updated contents");
        await manager.CommitContinuousDeliveryAsync([Page(root, new ChangeFeedEventDto(ChangeFeedEventKind.Modified, file, false, null))], "hidden-change", CancellationToken.None);
        Assert.Equal(new FileInfo(file).Length, manager.CurrentSearchState.Get("system").Single().SizeBytes);
    }

    [Fact]
    public async Task HiddenJunctionCycleStopsAtTheAliasButSiblingAliasesRemainVisible()
    {
        using var space = new TemporaryDirectory();
        var root = space.CreateDirectory("files");
        var target = space.CreateDirectory("target");
        space.CreateFile(@"target\child.txt", "contents");
        var loop = Path.Combine(root, "loop");
        var first = Path.Combine(root, "first");
        var second = Path.Combine(root, "second");
        WindowsDirectoryLink.CreateJunction(loop, root);
        WindowsDirectoryLink.CreateJunction(first, target);
        WindowsDirectoryLink.CreateJunction(second, target);
        try
        {
            File.SetAttributes(loop, File.GetAttributes(loop) | FileAttributes.Hidden);
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            using var manager = Create(Path.Combine(space.Path, "index.db"));
            await manager.InitializeLiveAsync([root], null, new(new Channel(), new IndexManagerChangeFeedTarget(manager)), deadline.Token);
            Assert.True(manager.CurrentSearchState.ContainsPath(loop));
            Assert.False(manager.CurrentSearchState.ContainsPath(Path.Combine(loop, "loop")));
            Assert.True(manager.CurrentSearchState.ContainsPath(Path.Combine(first, "child.txt")));
            Assert.True(manager.CurrentSearchState.ContainsPath(Path.Combine(second, "child.txt")));
            Assert.DoesNotContain(loop, manager.LiveWatcherRoots);
        }
        finally
        {
            WindowsDirectoryLink.Delete(loop);
            WindowsDirectoryLink.Delete(first);
            WindowsDirectoryLink.Delete(second);
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BootstrapDirectoryEventsDurablyRepairChildrenMissingFromTheInventory(bool renamed)
    {
        using var space = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(space.Path, "files")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "middle")).FullName;
        File.WriteAllText(Path.Combine(folder, "known.txt"), "known");
        File.WriteAllText(Path.Combine(folder, "ghost.txt"), "gone later");
        var database = Path.Combine(space.Path, "index.db");
        using var manager = Create(database);
        await manager.InitializeLiveAsync([root], null,
            new(new Channel { FailDrainAfter = 1 }, new IndexManagerChangeFeedTarget(manager)), CancellationToken.None);
        File.WriteAllText(Path.Combine(folder, "missing.txt"), "missed by initial inventory");
        File.Delete(Path.Combine(folder, "ghost.txt"));
        var target = renamed ? Path.Combine(root, "final") : folder;
        if (renamed) Directory.Move(folder, target);
        var events = renamed
            ? new[] { new ChangeFeedEventDto(ChangeFeedEventKind.Renamed, folder, true, Path.Combine(root, "first")),
                new ChangeFeedEventDto(ChangeFeedEventKind.Renamed, target, true, folder) }
            : [new ChangeFeedEventDto(ChangeFeedEventKind.Created, target, true, null)];
        await manager.CommitContinuousDeliveryAsync([Page(root, events)], "bootstrap-churn", CancellationToken.None);
        Assert.True(manager.PendingRepairCount > 0);
        var store = Path.GetDirectoryName(Assert.Single(Directory.GetFiles(database + ".live", "head.bin", SearchOption.AllDirectories)))!;
        Assert.NotEmpty(LiveCatalogStore.ReadHead(store).PendingRepairs ?? []);
        manager.CompleteLiveBootstrap();
        Assert.True(BootstrapPending(manager));
        await manager.RetryLiveRepairsAsync(CancellationToken.None);
        Assert.Equal(0, manager.PendingRepairCount);
        Assert.True(manager.CurrentSearchState.ContainsPath(Path.Combine(target, "known.txt")));
        Assert.True(manager.CurrentSearchState.ContainsPath(Path.Combine(target, "missing.txt")));
        Assert.False(manager.CurrentSearchState.ContainsPath(Path.Combine(target, "ghost.txt")));
        if (renamed) Assert.False(manager.CurrentSearchState.ContainsPath(folder));
        manager.CompleteLiveBootstrap();
        Assert.False(BootstrapPending(manager));
    }

    private static bool BootstrapPending(IndexManager manager)
    {
        var bytes = File.ReadAllBytes(manager.DatabasePath);
        using var control = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 32));
        return control.RootElement.GetProperty("BootstrapPending").GetBoolean();
    }

    [Fact]
    public void ModificationCoalescingPreservesStructuralEventBoundaries()
    {
        FileChangeEvent Event(FileChangeType type, string path, bool directory = false) => new() { ChangeType = type, FullPath = path, IsDirectory = directory };
        var first = Event(FileChangeType.Modified, @"C:\files\one.txt");
        var other = Event(FileChangeType.Modified, @"C:\files\other.txt");
        var latest = Event(FileChangeType.Modified, @"C:\FILES\one.txt");
        var delete = Event(FileChangeType.Deleted, first.FullPath);
        var create = Event(FileChangeType.Created, first.FullPath);
        var after = Event(FileChangeType.Modified, first.FullPath);
        var rename = new FileChangeEvent { ChangeType = FileChangeType.Renamed, FullPath = @"C:\files\new.txt", OldPath = first.FullPath };
        var directory = Event(FileChangeType.Modified, @"C:\files", true);
        Assert.Equal(new[] { latest, other, delete, create, after, rename, first, directory, latest },
            IndexManager.CoalesceLiveModifications([first, other, latest, delete, create, after, rename, first, directory, latest]));
    }

    [Fact]
    public async Task DeliveryWritesOnlyTheAffectedRootAndReplaysIdempotently()
    {
        using var space = new TemporaryDirectory();
        var a = Directory.CreateDirectory(Path.Combine(space.Path, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(space.Path, "b")).FullName;
        var path = Path.Combine(a, "report.txt"); File.WriteAllText(path, "old");
        var database = Path.Combine(space.Path, "index.db");
        using var manager = Create(database);
        await manager.InitializeLiveAsync([a, b], null, new(new Channel(), new IndexManagerChangeFeedTarget(manager)), CancellationToken.None);
        var stores = Directory.GetFiles(database + ".live", "head.bin", SearchOption.AllDirectories).Select(file => Path.GetDirectoryName(file)!).ToArray();
        var before = stores.ToDictionary(directory => directory, LiveCatalogStore.ReadHead);
        File.WriteAllText(path, "new contents");
        var events = Enumerable.Range(0, 25).Select(_ => new ChangeFeedEventDto(ChangeFeedEventKind.Modified, path, false, null)).ToArray();
        await manager.CommitContinuousDeliveryAsync([Page(a, events)], "changed-a", CancellationToken.None);
        foreach (var directory in stores)
            Assert.Equal(before[directory].Sequence + (before[directory].Root == a ? 1 : 0), LiveCatalogStore.ReadHead(directory).Sequence);
        Assert.Equal(new FileInfo(path).Length, manager.CurrentSearchState.Get("report").Single().SizeBytes);
        await manager.CommitContinuousDeliveryAsync([Page(a, events)], "changed-a", CancellationToken.None);
        foreach (var directory in stores)
            Assert.Equal(before[directory].Sequence + (before[directory].Root == a ? 1 : 0), LiveCatalogStore.ReadHead(directory).Sequence);
    }

    [Fact]
    public async Task CatalogInsideIndexedRootNeverIndexesOrRewritesItself()
    {
        using var space = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(space.Path, "files")).FullName;
        var document = Path.Combine(root, "document.txt"); File.WriteAllText(document, "document");
        var database = Path.Combine(root, "index.db");
        using (var manager = Create(database))
        {
            await manager.InitializeLiveAsync([root], null, new(new Channel(), new IndexManagerChangeFeedTarget(manager)), CancellationToken.None);
            Assert.Equal(1, manager.GetStats().FileCount); Assert.Equal(1, manager.GetStats().DirectoryCount);
            Assert.False(manager.CurrentSearchState.ContainsPath(database + ".live"));
            var headPath = Directory.GetFiles(database + ".live", "head.bin", SearchOption.AllDirectories).Single();
            var before = File.ReadAllBytes(headPath);
            await manager.CommitContinuousDeliveryAsync([Page(root, new ChangeFeedEventDto(ChangeFeedEventKind.Created, headPath, false, null))], "self-write", CancellationToken.None);
            Assert.Equal(before, File.ReadAllBytes(headPath));
            Assert.Equal(1, manager.GetStats().FileCount); Assert.Equal(0, manager.PendingRepairCount);
            var moved = Path.Combine(database + ".live", "moved.txt"); File.Move(document, moved);
            await manager.CommitContinuousDeliveryAsync([Page(root, new ChangeFeedEventDto(ChangeFeedEventKind.Renamed, moved, false, document))], "into-storage", CancellationToken.None);
            Assert.False(manager.CurrentSearchState.ContainsPath(document));
            File.Move(moved, document);
            await manager.CommitContinuousDeliveryAsync([Page(root, new ChangeFeedEventDto(ChangeFeedEventKind.Renamed, document, false, moved))], "out-of-storage", CancellationToken.None);
            Assert.True(manager.CurrentSearchState.ContainsPath(document));
        }
        using var cached = Create(database);
        await cached.InitializeLiveAsync([root], null, new(new Channel(), new IndexManagerChangeFeedTarget(cached)), CancellationToken.None);
        Assert.Equal(1, cached.GetStats().FileCount); Assert.False(cached.CurrentSearchState.ContainsPath(database + ".live"));
    }

    [Fact]
    public async Task JunctionContentsKeepLiveUpdatesThroughOnlyTheUnsupportedScopeWatcher()
    {
        using var space = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(space.Path, "files")).FullName;
        var target = Directory.CreateDirectory(Path.Combine(space.Path, "external")).FullName;
        File.WriteAllText(Path.Combine(target, "existing.txt"), "existing");
        var link = Path.Combine(root, "link");
        var start = new System.Diagnostics.ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
        start.ArgumentList.Add("/c"); start.ArgumentList.Add("mklink"); start.ArgumentList.Add("/J"); start.ArgumentList.Add(link); start.ArgumentList.Add(target);
        using (var process = System.Diagnostics.Process.Start(start)!)
        { await process.WaitForExitAsync(); Assert.Equal(0, process.ExitCode); }
        try
        {
            using var manager = Create(Path.Combine(space.Path, "index.db"));
            await manager.InitializeLiveAsync([root], null, new(new Channel(), new IndexManagerChangeFeedTarget(manager)), CancellationToken.None);
            Assert.Equal(link, Assert.Single(manager.LiveWatcherRoots)); Assert.True(manager.IsWatching);
            Assert.True(manager.CurrentSearchState.ContainsPath(Path.Combine(link, "existing.txt")));
            var watched = Path.Combine(link, "arrived.txt"); File.WriteAllText(Path.Combine(target, "arrived.txt"), "arrived");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            while (!manager.CurrentSearchState.ContainsPath(watched)) await Task.Delay(25, timeout.Token);
            Assert.Equal(2, manager.GetStats().FileCount); Assert.Equal(0, manager.ReconciliationRunCount);
        }
        finally { Directory.Delete(link); }
    }

    [Fact]
    public async Task TransientFaultDoesNotScanButConfirmedJournalLossRebuildsOnlyItsRoot()
    {
        using var space = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(space.Path, "files")).FullName;
        var original = Path.Combine(root, "old.txt"); File.WriteAllText(original, "old");
        using var manager = Create(Path.Combine(space.Path, "index.db"));
        await manager.InitializeLiveAsync([root], null, new(new Channel(), new IndexManagerChangeFeedTarget(manager)), CancellationToken.None);
        var old = manager.CurrentSearchState; var added = Path.Combine(root, "new.txt"); File.Delete(original); File.WriteAllText(added, "new");
        await manager.CommitContinuousDeliveryAsync([new(root, [], ChangeFeedGapReason.None, ChangeFeedFaultReason.JournalTemporarilyUnavailable, false, false)], "fault", CancellationToken.None);
        await manager.RetryLiveRepairsAsync(CancellationToken.None);
        Assert.Equal(0, manager.PendingRepairCount); Assert.True(manager.CurrentSearchState.ContainsPath(original));
        await manager.CommitContinuousDeliveryAsync([new(root, [], ChangeFeedGapReason.CursorOutsideJournal, ChangeFeedFaultReason.None, false, false)], "gap", CancellationToken.None);
        Assert.Equal(1, manager.PendingRepairCount);
        await manager.RetryLiveRepairsAsync(CancellationToken.None);
        Assert.Equal(0, manager.PendingRepairCount); Assert.True(manager.CurrentSearchState.ContainsPath(added)); Assert.False(manager.CurrentSearchState.ContainsPath(original));
        Assert.True(old.ContainsPath(original));
        manager.IncrementOpenCount(added); Assert.Equal(1, manager.CurrentSearchState.Get("new").Single().OpenCount);
    }

    [Fact]
    public async Task IncompleteFinalDrainKeepsBootstrapMarkerEvenWhenTheQueueIsEmpty()
    {
        using var space = new TemporaryDirectory(); var root = Directory.CreateDirectory(Path.Combine(space.Path, "files")).FullName;
        using var manager = Create(Path.Combine(space.Path, "index.db"));
        var bridge = new ChangeFeedIndexBridge(new Channel { FailDrainAfter = 1 }, new IndexManagerChangeFeedTarget(manager));
        await manager.InitializeLiveAsync([root], null, bridge, CancellationToken.None);
        var bytes = File.ReadAllBytes(manager.DatabasePath);
        using var control = System.Text.Json.JsonDocument.Parse(bytes.AsMemory(0, bytes.Length - 32));
        Assert.True(control.RootElement.GetProperty("BootstrapPending").GetBoolean());
        Assert.True(manager.IsInitialized);
    }

    [Fact]
    public async Task FreshAndCachedCatalogPreserveFeaturesAndApplyDurableEventsWithoutSqlOrFullRescan()
    {
        using var space = new TemporaryDirectory();
        var root = Directory.CreateDirectory(Path.Combine(space.Path, "files")).FullName;
        var folder = Directory.CreateDirectory(Path.Combine(root, "Documents")).FullName;
        var original = Path.Combine(folder, "İSTANBUL-rapor.pdf"); File.WriteAllText(original, "first");
        var hidden = Path.Combine(root, "hidden.txt"); File.WriteAllText(hidden, "hidden"); File.SetAttributes(hidden, FileAttributes.Hidden);
        var database = Path.Combine(space.Path, "index.db");
        var renamedFolder = Path.Combine(root, "Work"); var renamed = Path.Combine(renamedFolder, Path.GetFileName(original));
        ISearchStateReader old;
        using (var manager = Create(database))
        {
            var channel = new Channel(); var bridge = new ChangeFeedIndexBridge(channel, new IndexManagerChangeFeedTarget(manager));
            await manager.InitializeLiveAsync([root], null, bridge, CancellationToken.None);
            Assert.False(File.Exists(database)); Assert.False(manager.IsWatching);
            Assert.Equal(2, manager.GetStats().FileCount); Assert.Equal(2, manager.GetStats().DirectoryCount);
            Assert.True(manager.CurrentSearchState.ContainsPath(hidden));
            old = manager.CreateSearchState();
            Assert.Single(old.Get("istanbul")); Assert.Single(old.GetPartial("rap")); Assert.Single(old.GetFuzzy("rappr", 1));
            manager.IncrementOpenCount(original);
            Assert.Equal(1, manager.GetNode(original)!.Metadata!.OpenCount);
            Assert.Equal(0, old.Get("rapor").Single().OpenCount);
            Directory.Move(folder, renamedFolder);
            await manager.CommitContinuousDeliveryAsync([Page(root, new ChangeFeedEventDto(ChangeFeedEventKind.Renamed, renamedFolder, true, folder))], "rename-batch", CancellationToken.None);
            Assert.True(manager.CurrentSearchState.ContainsPath(renamed)); Assert.False(manager.CurrentSearchState.ContainsPath(original));
            Assert.Equal(1, manager.CurrentSearchState.Get("rapor").Single().OpenCount);
            Assert.True(old.ContainsPath(original));
            Assert.Equal(renamedFolder, manager.GetNode(renamed)!.Parent!.FullPath);
            File.WriteAllText(renamed, "new file occupying a previously deleted path");
            await manager.CommitContinuousDeliveryAsync([Page(root, new ChangeFeedEventDto(ChangeFeedEventKind.Deleted, renamed, false, null))], "stale-delete", CancellationToken.None);
            Assert.True(manager.CurrentSearchState.ContainsPath(renamed));
            Assert.Equal(new FileInfo(renamed).Length, manager.CurrentSearchState.Get("rapor").Single().SizeBytes);
            Assert.Equal(0, manager.ReconciliationRunCount);
            Assert.DoesNotContain(channel.Requests, request => request.Kind is ChangeFeedRequestKind.HoldLease or ChangeFeedRequestKind.DrainAndHoldLease);
        }
        using (var reopened = Create(database))
        {
            var bridge = new ChangeFeedIndexBridge(new Channel(), new IndexManagerChangeFeedTarget(reopened));
            await reopened.InitializeLiveAsync([root], null, bridge, CancellationToken.None);
            Assert.Equal(1, reopened.CurrentSearchState.Get("rapor").Single().OpenCount);
            Assert.True(old.ContainsPath(original));
            await reopened.CommitContinuousDeliveryAsync([Page(root, new ChangeFeedEventDto(ChangeFeedEventKind.Deleted, renamed, false, null))], "stale-delete", CancellationToken.None);
            Assert.True(reopened.CurrentSearchState.ContainsPath(renamed));
            File.Delete(renamed);
            await reopened.CommitContinuousDeliveryAsync([Page(root, new ChangeFeedEventDto(ChangeFeedEventKind.Deleted, renamed, false, null))], "real-delete", CancellationToken.None);
            Assert.Empty(reopened.CurrentSearchState.Get("rapor"));
            Assert.Equal(1, reopened.GetStats().FileCount);
            Assert.Equal(0, reopened.PendingRepairCount);
            Assert.Equal(0, reopened.ReconciliationRunCount);
        }
        File.SetAttributes(hidden, FileAttributes.Normal);
    }

    [Fact]
    public async Task MultipleRootsSupportMoveAndGlobalFuzzySemantics()
    {
        using var space = new TemporaryDirectory();
        var a = Directory.CreateDirectory(Path.Combine(space.Path, "a")).FullName;
        var b = Directory.CreateDirectory(Path.Combine(space.Path, "b")).FullName;
        var file = Path.Combine(a, "report.txt"); File.WriteAllText(file, "1");
        File.WriteAllText(Path.Combine(b, "reports.txt"), "2");
        using var manager = Create(Path.Combine(space.Path, "index.db"));
        await manager.InitializeLiveAsync([a, b], null, new(new Channel(), new IndexManagerChangeFeedTarget(manager)), CancellationToken.None);
        Assert.Single(manager.CurrentSearchState.GetFuzzy("report", 1));
        Assert.Equal(2, manager.GetIndexedRootNodes().Count);
        var target = Path.Combine(b, "report.txt"); File.Move(file, target);
        await manager.CommitContinuousDeliveryAsync([Page(a, new ChangeFeedEventDto(ChangeFeedEventKind.Deleted, file, false, null)), Page(b, new ChangeFeedEventDto(ChangeFeedEventKind.Created, target, false, null))], "move", CancellationToken.None);
        Assert.True(manager.CurrentSearchState.ContainsPath(target)); Assert.False(manager.CurrentSearchState.ContainsPath(file));
        Assert.Equal(2, manager.GetStats().FileCount);
        Assert.Equal(CompactSearchState.Create(manager.CurrentSearchState.GetAllItems(), new BasicTokenizer()).TokenCount, manager.GetStats().TokenCount);
    }

    private static IndexManager Create(string database)
    {
        var manager = IndexManager.CreateWithDatabasePath(database, enforceMeasurementPathSafety: false, layout: SearchStateLayout.Compact);
        manager.EnableLiveCatalog(); return manager;
    }
    private static ChangeFeedRootPageDto Page(string root, params ChangeFeedEventDto[] events) => new(root, events, ChangeFeedGapReason.None, ChangeFeedFaultReason.None, false, false);
    private sealed class LinkInventory(string[] links) : IIndexInventorySource, IIndexInventorySession
    {
        public Task<IIndexInventorySession?> ReadAsync(IReadOnlyList<string> roots, Action<IndexInventoryEntry> receive, CancellationToken cancellationToken)
        {
            foreach (var root in roots) receive(new(root, true, FileAttributes.Directory, 0, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks));
            foreach (var link in links) receive(new(link, true, FileAttributes.Directory | FileAttributes.ReparsePoint, 0, DateTime.UtcNow.Ticks, DateTime.UtcNow.Ticks));
            return Task.FromResult<IIndexInventorySession?>(this);
        }
        public Task<bool> ValidateAsync(CancellationToken cancellationToken) => Task.FromResult(true);
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
    private sealed class Channel : IChangeFeedRequestChannel
    {
        internal List<ChangeFeedRequest> Requests { get; } = [];
        private readonly List<string> _roots = [];
        internal int FailDrainAfter = int.MaxValue;
        private int _drains;
        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken cancellationToken)
        {
            Requests.Add(request);
            if (request.Kind == ChangeFeedRequestKind.DrainContinuous && ++_drains > FailDrainAfter)
                return Task.FromResult(ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, "injected"));
            if (request.Kind == ChangeFeedRequestKind.GetCapabilities) return Task.FromResult(ChangeFeedResponse.Granted("continuous-usn-v1"));
            if (request.Kind == ChangeFeedRequestKind.PrepareContinuousRoot)
            {
                if (!_roots.Contains(request.RootPath!)) _roots.Add(request.RootPath!);
                return Task.FromResult(ChangeFeedResponse.Ok(_roots.ToArray()));
            }
            return Task.FromResult(request.Kind == ChangeFeedRequestKind.Pull
                ? ChangeFeedResponse.Delivered(new([], false, null, null)) : ChangeFeedResponse.Ok());
        }
    }
}
