using System.Runtime.Versioning;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Search.Paged;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using System.Security.Principal;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

[SupportedOSPlatform("windows")]
public sealed class SharedCatalogIntegrationTests
{
    [Fact]
    public async Task AllNamesCatalogDeliveryIsSeparateFromCallerFilteredPipeDelivery()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "OmniSpot.NamePolicy." + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temporary, "input");
        var child = Path.Combine(root, "private", "report.txt");
        Directory.CreateDirectory(Path.GetDirectoryName(child)!);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(Path.Combine(temporary, "store"), sid, sid);
        var store = new FileSystemChangeFeedStore(layout);
        using var caller = await ChangeFeedCapturedCallerTests.CaptureAsync();
        using var admission = new ChangeFeedAdmissionService(new(new UsnFileSystemIdentityProbe()), _ => store,
            authorizerFactory: path => new(path, _ => false));
        try
        {
            Assert.Equal(ChangeFeedResponseStatus.Ok, admission.Handle(caller,
                new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, RootPath: root)).Status);
            using (store.EnterOwnerScope(default))
            {
                var subscribed = Assert.Single(store.ReadSubscription()!.Roots);
                store.Enqueue(subscribed.Identity.VolumeId, 1, 10, 20,
                    [new(root, ChangeFeedBatch.Ok([new(ChangeFeedEventKind.Created, child, false)]), subscribed.Generation)]);
            }
            var request = new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.Pull);
            var filtered = admission.Handle(caller, request).Delivery!;
            Assert.Empty(Assert.Single(filtered.Roots).Events);
            Assert.True(Assert.Single(filtered.Roots).AuthorizationGap);
            var allNames = admission.HandleCatalog(caller, request, true, default).Delivery!;
            Assert.Equal(child, Assert.Single(Assert.Single(allNames.Roots).Events).Path);
            Assert.False(Assert.Single(allNames.Roots).AuthorizationGap);
        }
        finally { Directory.Delete(temporary, true); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InterruptedManifestCommitReplaysDiscoveredJunction(bool producerGap)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "OmniSpot.SharedReplay." + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temporary, "input");
        var target = Path.Combine(temporary, "target");
        var link = Path.Combine(root, "container", "link");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(target, "visible.txt"), "one");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(Path.Combine(temporary, "store"), sid, sid);
        var store = new FileSystemChangeFeedStore(layout);
        using var captured = await ChangeFeedCapturedCallerTests.CaptureAsync();
        using var admission = new ChangeFeedAdmissionService(new(new UsnFileSystemIdentityProbe()), _ => store,
            handoffDrainer: (_, _) => true, sharedCatalogLayout: _ => layout);
        var channel = new TestChannel(admission, captured);
        var control = Path.Combine(temporary, "ui.live", "control.bin");
        var initial = new SharedIndexLifecycleService(channel, new Locations(root), IndexScanMethod.SelectedFolders, control);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            await initial.InitializeAsync(deadline.Token);
            var failed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            initial.Error += _ => failed.TrySetResult();
            var manifestBlocker = Path.Combine(initial.CatalogDirectory!, "current.tmp");
            Directory.CreateDirectory(manifestBlocker);
            Directory.CreateDirectory(Path.GetDirectoryName(link)!);
            WindowsDirectoryLink.CreateJunction(link, target);
            using (store.EnterOwnerScope(deadline.Token))
            {
                var subscribed = Assert.Single(store.ReadSubscription()!.Roots);
                var batch = producerGap ? ChangeFeedBatch.Gap(ChangeFeedGapReason.DeliveryQueueOverflow) :
                    ChangeFeedBatch.Ok([new(ChangeFeedEventKind.Created, Path.GetDirectoryName(link)!, true)]);
                store.Enqueue(subscribed.Identity.VolumeId, 1, 10, 20, [new(root, batch, subscribed.Generation)]);
            }
            await failed.Task.WaitAsync(deadline.Token);
            initial.Dispose();
            Directory.Delete(manifestBlocker);
            using var reopened = new SharedIndexLifecycleService(channel, new Locations(root), IndexScanMethod.SelectedFolders, control);
            await reopened.InitializeAsync(deadline.Token);
            var state = reopened.CreateSearchState();
            using var lease = state as IDisposable;
            Assert.True(state.ContainsPath(Path.Combine(link, "visible.txt")));
            Assert.Equal(1, reopened.GetStats().FileCount);
        }
        finally
        {
            initial.Dispose();
            admission.Dispose();
            WindowsDirectoryLink.Delete(link);
            Directory.Delete(temporary, true);
        }
    }

    [Fact]
    public async Task JunctionCatalogUsesSeparateIdentitySpaceAndDoesNotFollowCycles()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "OmniSpot.SharedLinks." + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temporary, "input");
        var target = Path.Combine(temporary, "target");
        var output = Path.Combine(temporary, "output");
        Directory.CreateDirectory(root);
        Directory.CreateDirectory(target);
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(target, "visible.txt"), "one");
        var link = Path.Combine(root, "link");
        var back = Path.Combine(target, "back");
        WindowsDirectoryLink.CreateJunction(link, target);
        WindowsDirectoryLink.CreateJunction(back, root);
        using var caller = await ChangeFeedCapturedCallerTests.CaptureAsync();
        var parts = new List<SharedCatalogBuilder.Part>();
        try
        {
            parts = new SharedCatalogBuilder(caller, output, output, (_, _) => { }).ReadDirectories([root], default);
            Assert.Equal(2, parts.Count);
            Assert.All(parts, part =>
            {
                using var catalog = part.Store.Acquire();
                Assert.Equal(0, catalog.Data.SortedCount);
            });
            using var state = new PagedSearchState(parts.Select(part => (part.Store, part.ExcludeRoot)));
            using var query = state.Acquire();
            Assert.Equal(4, query.ItemCount);
            Assert.True(query.ContainsPath(Path.Combine(link, "visible.txt")));
            Assert.True(query.ContainsPath(Path.Combine(link, "back")));
            Assert.Empty(query.GetChildren(Path.Combine(link, "back")));
            parts[0].Store.Commit(catalog => new SharedCatalogReconciler(caller, output, allNames: true)
                .Repair(catalog, Path.Combine(link, "visible.txt"), default), 0);
            using var mainOnly = parts[0].Store.Query();
            Assert.False(mainOnly.ContainsPath(Path.Combine(link, "visible.txt")));
        }
        finally
        {
            foreach (var part in parts) part.Store.Dispose();
            WindowsDirectoryLink.Delete(back);
            WindowsDirectoryLink.Delete(link);
            Directory.Delete(temporary, true);
        }
    }

    [Fact]
    public async Task ServiceSessionFeedsNormalLifecycleAndReopensWithoutScanning()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "OmniSpot.SharedSession." + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temporary, "input");
        Directory.CreateDirectory(root);
        await File.WriteAllTextAsync(Path.Combine(root, "alpha.txt"), "one");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(Path.Combine(temporary, "store"), sid, sid);
        var store = new FileSystemChangeFeedStore(layout);
        using var captured = await ChangeFeedCapturedCallerTests.CaptureAsync();
        using var admission = new ChangeFeedAdmissionService(new(new UsnFileSystemIdentityProbe()), _ => store,
            handoffDrainer: (_, _) => true, sharedCatalogLayout: _ => layout);
        var channel = new TestChannel(admission, captured);
        var control = Path.Combine(temporary, "ui.live", "control.bin");
        var initial = new SharedIndexLifecycleService(channel, new Locations(root), IndexScanMethod.SelectedFolders, control);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            var ready = await initial.InitializeAsync(deadline.Token);
            Assert.Equal(1, ready.Stats.FileCount);
            Assert.Equal("alpha.txt", Assert.Single(Assert.Single(initial.GetIndexedRoots()).Children).Name);
            var previous = initial.CreateSearchState();
            using var queryLease = previous as IDisposable;
            File.Move(Path.Combine(root, "alpha.txt"), Path.Combine(root, "beta.txt"));
            using (store.EnterOwnerScope(deadline.Token))
            {
                var subscribed = Assert.Single(store.ReadSubscription()!.Roots);
                store.Enqueue(subscribed.Identity.VolumeId, 1, 10, 20,
                    [new(root, ChangeFeedBatch.Ok([new(ChangeFeedEventKind.Renamed, Path.Combine(root, "beta.txt"), false,
                        Path.Combine(root, "alpha.txt"))]), subscribed.Generation)]);
            }
            Assert.True(await initial.EnsureSyncedAsync(root, deadline.Token));
            Assert.Equal("beta.txt", Assert.Single(Assert.Single(initial.GetIndexedRoots()).Children).Name);
            Assert.True(previous.ContainsPath(Path.Combine(root, "alpha.txt")));
            initial.RecordOpened(Path.Combine(root, "beta.txt"));
            Assert.True(await initial.EnsureSyncedAsync(root, deadline.Token));
            var catalogDirectory = initial.CatalogDirectory!;
            initial.Dispose();
            var cachePhases = new List<string>();
            var beforeParts = Directory.GetDirectories(catalogDirectory, "part-*").Length;
            using var sameMethod = new SharedIndexLifecycleService(channel, new Locations(root), IndexScanMethod.Mft, control);
            sameMethod.ProgressChanged += progress => cachePhases.Add(progress.Status);
            await sameMethod.InitializeAsync(deadline.Token);
            Assert.DoesNotContain(cachePhases, phase => phase.Contains("Klasörler okunuyor", StringComparison.Ordinal));
            Assert.Equal(1, sameMethod.GetStats().FileCount);
            Assert.Equal(beforeParts, Directory.GetDirectories(catalogDirectory, "part-*").Length);
        }
        finally
        {
            initial.Dispose();
            admission.Dispose();
            Directory.Delete(temporary, true);
        }
    }

    [Fact]
    public async Task CachedCatalogReopensWhenALinkedDirectoryVanishedWhileClosed()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "OmniSpot.SharedStaleLink." + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temporary, "input");
        var target = Path.Combine(temporary, "target");
        var container = Path.Combine(root, "container");
        var link = Path.Combine(container, "link");
        Directory.CreateDirectory(container);
        Directory.CreateDirectory(target);
        await File.WriteAllTextAsync(Path.Combine(root, "kept.txt"), "kept");
        await File.WriteAllTextAsync(Path.Combine(target, "linked.txt"), "linked");
        WindowsDirectoryLink.CreateJunction(link, target);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(Path.Combine(temporary, "store"), sid, sid);
        var store = new FileSystemChangeFeedStore(layout);
        using var captured = await ChangeFeedCapturedCallerTests.CaptureAsync();
        using var admission = new ChangeFeedAdmissionService(new(new UsnFileSystemIdentityProbe()), _ => store,
            handoffDrainer: (_, _) => true, sharedCatalogLayout: _ => layout);
        var channel = new TestChannel(admission, captured);
        var control = Path.Combine(temporary, "ui.live", "control.bin");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using (var initial = new SharedIndexLifecycleService(channel, new Locations(root), IndexScanMethod.SelectedFolders, control))
            {
                await initial.InitializeAsync(deadline.Token);
                var before = initial.CreateSearchState();
                using (before as IDisposable)
                    Assert.True(before.ContainsPath(Path.Combine(link, "linked.txt")));
            }
            WindowsDirectoryLink.Delete(link);
            Directory.Delete(container);

            using var reopened = new SharedIndexLifecycleService(channel, new Locations(root), IndexScanMethod.SelectedFolders, control);
            await reopened.InitializeAsync(deadline.Token);
            var state = reopened.CreateSearchState();
            using var lease = state as IDisposable;
            Assert.True(state.ContainsPath(Path.Combine(root, "kept.txt")));
            Assert.False(state.ContainsPath(Path.Combine(link, "linked.txt")));
            Assert.Equal(1, reopened.GetStats().FileCount);
        }
        finally
        {
            admission.Dispose();
            if (Directory.Exists(link)) WindowsDirectoryLink.Delete(link);
            Directory.Delete(temporary, true);
        }
    }

    [Fact]
    public async Task CachedCatalogPausesTheProducerUntilDeliveryIsAcknowledged()
    {
        var temporary = Path.Combine(Path.GetTempPath(), "OmniSpot.SharedDelivery." + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temporary, "input");
        Directory.CreateDirectory(root);
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User!.Value;
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(Path.Combine(temporary, "store"), sid, sid);
        var store = new FileSystemChangeFeedStore(layout);
        using var captured = await ChangeFeedCapturedCallerTests.CaptureAsync();
        var control = Path.Combine(temporary, "ui.live", "control.bin");
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        try
        {
            using (var firstAdmission = new ChangeFeedAdmissionService(new(new UsnFileSystemIdentityProbe()), _ => store,
                handoffDrainer: (_, _) => true, sharedCatalogLayout: _ => layout))
            using (var first = new SharedIndexLifecycleService(new TestChannel(firstAdmission, captured),
                new Locations(root), IndexScanMethod.SelectedFolders, control))
                await first.InitializeAsync(deadline.Token);
            var path = Path.Combine(root, "new.txt");
            await File.WriteAllTextAsync(path, "new");
            var subscribed = Assert.Single(store.ReadSubscription()!.Roots);
            store.Enqueue(subscribed.Identity.VolumeId, 1, 10, 20,
                [new(root, ChangeFeedBatch.Ok([new(ChangeFeedEventKind.Created, path, false)]), subscribed.Generation)]);
            var runner = new UsnDrainRunner(layout, store, new UsnVolumeJournalReaderFactory(), new UsnFileSystemIdentityProbe());
            var projections = 0;
            using var admission = new ChangeFeedAdmissionService(new(new UsnFileSystemIdentityProbe()), _ => store,
                authorizerFactory: folder => new(folder, _ =>
                {
                    Assert.Equal(UsnDrainOutcome.Busy, runner.Run(deadline.Token).Outcome);
                    Interlocked.Increment(ref projections);
                    return true;
                }), handoffDrainer: (_, _) => true, sharedCatalogLayout: _ => layout,
                catalogBootstrap: (_, ct) => runner.BeginBootstrap(ct));
            using var reopened = new SharedIndexLifecycleService(new TestChannel(admission, captured),
                new Locations(root), IndexScanMethod.SelectedFolders, control);
            await reopened.InitializeAsync(deadline.Token);
            Assert.True(projections > 0);
            Assert.Empty(store.ReadPending().Entries);
            Assert.Equal(1, reopened.GetStats().FileCount);
            using var released = runner.BeginBootstrap(deadline.Token);
        }
        finally { Directory.Delete(temporary, true); }
    }

    private sealed class TestChannel(ChangeFeedAdmissionService admission, ChangeFeedCaller caller) : IChangeFeedRequestChannel
    {
        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken ct) => Task.FromResult(admission.Handle(caller, request, ct));
    }
    private sealed class Locations(string path) : IIndexedLocationProvider
    {
        public IndexLocations Resolve() => new(path, [path]);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task BuildRenameStaleDeleteRepairAndDurableReceiptUseOneCatalog(bool portable)
    {
        var temporary = Path.Combine(Path.GetTempPath(), "OmniSpot.SharedCatalog." + Guid.NewGuid().ToString("N"));
        var root = Path.Combine(temporary, "input");
        var output = Path.Combine(temporary, "output");
        Directory.CreateDirectory(Path.Combine(root, "before"));
        Directory.CreateDirectory(output);
        await File.WriteAllTextAsync(Path.Combine(root, "before", "rapor.txt"), "before");
        using var caller = await ChangeFeedCapturedCallerTests.CaptureAsync();
        var builder = new SharedCatalogBuilder(caller, output, output, (_, _) => { });
        var parts = portable ? builder.ReadPortableDirectories([root], default) : builder.ReadDirectories([root], default);
        try
        {
            var store = Assert.Single(parts).Store;
            using var original = store.Acquire();
            Assert.Equal(3, original.Data.EntryCount);
            var id = new Reader(original).FindPath(Path.Combine(root, "before", "rapor.txt"));
            Assert.NotEqual(0u, id);
            Directory.Move(Path.Combine(root, "before"), Path.Combine(root, "after"));
            var changes = new[] { new ChangeFeedEventDto(ChangeFeedEventKind.Renamed, Path.Combine(root, "after"), true, Path.Combine(root, "before")) };
            var reconcile = new SharedCatalogReconciler(caller, output, nativeIdentity: !portable);
            store.Commit(catalog => reconcile.Apply(catalog, changes, default), 11, "delivery-one");
            using (var updated = store.Acquire())
            {
                Assert.Equal(id, new Reader(updated).FindPath(Path.Combine(root, "after", "rapor.txt")));
                updated.Audit();
            }
            store.Commit(_ => throw new InvalidOperationException("Tekrar uygulanmamalı"), 11, "delivery-one");
            using (var reopened = new Catalog(Image.Open(original.Data.Directory)))
            {
                Assert.Equal("delivery-one", reopened.Data.DeliveryId);
                Assert.Equal(11, reopened.Data.Cursor);
            }
            var renamed = Path.Combine(root, "after", "rapor.txt");
            File.Delete(renamed);
            await File.WriteAllTextAsync(renamed, "new file survives stale delete");
            store.Commit(catalog => reconcile.Apply(catalog,
                [new(ChangeFeedEventKind.Deleted, renamed, false, null)], default), 12, "delivery-two");
            File.Delete(renamed);
            await File.WriteAllTextAsync(Path.Combine(root, "after", "new.txt"), "new");
            store.Commit(catalog => reconcile.Repair(catalog, Path.Combine(root, "after"), default), 13, "delivery-three");
            using var final = store.Acquire();
            Assert.Equal(3, final.Data.EntryCount);
            Assert.True(new Reader(final).TryGetItem(Path.Combine(root, "after", "new.txt"), out _));
            Assert.False(new Reader(final).TryGetItem(renamed, out _));
            final.Audit();
            Assert.True(new Reader(original).TryGetItem(Path.Combine(root, "before", "rapor.txt"), out _));
        }
        finally
        {
            foreach (var part in parts) part.Store.Dispose();
            Directory.Delete(temporary, true);
        }
    }
}
