using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class VolumeIndexLifecycleTests
{
    private const ulong Root = 5UL << 48 | 5;

    [Fact]
    public async Task NextPageIsRequestedBeforePreviousPageIsReported()
    {
        using var directory = new TemporaryDirectory();
        var channel = new PrefetchChannel();
        using var service = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path);
        var overlapped = false;
        service.ProgressChanged += progress =>
        {
            if (progress.ItemCount != 2 || !progress.Status.StartsWith("Disk taranıyor:")) return;
            overlapped = channel.NextRequested.Task.Wait(TimeSpan.FromSeconds(5));
            channel.ReleaseNext.TrySetResult();
        };
        var started = await service.InitializeAsync();
        Assert.True(overlapped);
        Assert.Equal(3, started.Stats.FileCount);
        Assert.Equal(["rapor.pdf"], Search(service, "rapor"));
    }

    [Fact]
    public async Task SelectedScanOptionsAreSentOnEachNewScan()
    {
        using var directory = new TemporaryDirectory();
        var channel = new FakeVolumeChannel();
        var options = new IndexBuildOptions(IndexScanWorkerMode.Manual, 4);
        using var service = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path, options);
        await service.InitializeAsync();
        Assert.Equal(options, Assert.Single(channel.ScanOptions));
        channel.JournalId = 99;
        await service.EnsureSyncedAsync(@"C:\");
        Assert.Equal(new[] { options, options }, channel.ScanOptions);
    }

    [Fact]
    public async Task FirstStartScansAndTheNextStartLoadsTheSnapshotWithoutScanning()
    {
        using var directory = new TemporaryDirectory();
        var channel = new FakeVolumeChannel();
        using (var first = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path))
        {
            var started = await first.InitializeAsync();
            Assert.Equal(1, channel.Scans);
            Assert.Equal(3, started.Stats.FileCount);
            Assert.Equal(["rapor.pdf"], Search(first, "rapor"));
        }
        using var second = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path);
        await second.InitializeAsync();
        Assert.Equal(1, channel.Scans);
        Assert.Equal(["rapor.pdf"], Search(second, "rapor"));
        Assert.Equal(@"C:\", Assert.Single(second.GetIndexedRoots()).FullPath);
    }

    [Fact]
    public async Task JournalChangesAreReconciledAndAnnounced()
    {
        using var directory = new TemporaryDirectory();
        var channel = new FakeVolumeChannel();
        using var service = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path);
        await service.InitializeAsync();
        var announced = 0;
        service.FileChanged += _ => Interlocked.Increment(ref announced);
        channel.Pending.Enqueue([
            new IndexRecordState(Reference(30), [Entry(30, Reference(20), "yeni-belge.docx")]),
            new IndexRecordState(Reference(21), [])
        ]);
        Assert.True(await service.EnsureSyncedAsync(@"C:\"));
        Assert.Equal(["yeni-belge.docx"], Search(service, "yeni"));
        Assert.Empty(Search(service, "rapor"));
        Assert.True(announced > 0);
        await service.EnsureSyncedAsync(@"C:\");
        Assert.Equal(110, channel.LastRequestedUsn);
    }

    [Fact]
    public async Task IdenticalJournalStatesAdvanceTheCheckpointWithoutAnnouncingAChange()
    {
        using var directory = new TemporaryDirectory();
        var channel = new FakeVolumeChannel();
        using var service = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path);
        await service.InitializeAsync();
        var announced = 0;
        SmartFileLauncher.Core.Models.FileChangeEvent? notification = null;
        service.FileChanged += change => { notification = change; Interlocked.Increment(ref announced); };
        var entry = channel.Entries.Single(item => item.Name == "rapor.pdf");
        channel.Pending.Enqueue([new(entry.FileReference, [entry])]);

        await service.EnsureSyncedAsync(@"C:\");
        await service.EnsureSyncedAsync(@"C:\");
        Assert.Equal(0, announced);
        Assert.Equal(110, channel.LastRequestedUsn);

        var renamed = entry with { Name = "yeni-rapor.pdf" };
        channel.Pending.Enqueue([new(entry.FileReference, [renamed])]);
        await service.EnsureSyncedAsync(@"C:\");
        Assert.Equal(1, announced);
        Assert.NotNull(notification);
        Assert.Equal(new[] { "rapor.pdf", "yeni-rapor.pdf" }, notification.AffectedNames!.Order().ToArray());
        Assert.False(notification.IsDirectory);
        Assert.Equal(["yeni-rapor.pdf"], Search(service, "rapor"));

        channel.Pending.Enqueue([new(entry.FileReference, [renamed])]);
        await service.EnsureSyncedAsync(@"C:\");
        Assert.Equal(1, announced);
    }

    [Fact]
    public async Task AJournalResetRescansAndARebuildMarkerForcesAScan()
    {
        using var directory = new TemporaryDirectory();
        var channel = new FakeVolumeChannel();
        using (var service = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path))
        {
            await service.InitializeAsync();
            channel.JournalId = 99;
            channel.Entries.Add(Entry(40, Reference(20), "sonradan.txt"));
            await service.EnsureSyncedAsync(@"C:\");
            Assert.Equal(2, channel.Scans);
            Assert.Equal(["sonradan.txt"], Search(service, "sonradan"));
        }
        File.WriteAllText(Path.Combine(directory.Path, "rebuild.request"), "");
        using var rebuilt = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path);
        await rebuilt.InitializeAsync();
        Assert.Equal(3, channel.Scans);
        Assert.False(File.Exists(Path.Combine(directory.Path, "rebuild.request")));
    }

    private static string[] Search(IIndexLifecycleService service, string query)
    {
        var state = service.CreateSearchState();
        return ((ILiteralNameQuerySnapshot)state).SearchLiteralNames(query, 50, ResultView.SearchDefault, default)
            .Select(result => result.Name).ToArray();
    }

    private static ulong Reference(ulong record) => 1UL << 48 | record;

    private static IndexEntry Entry(ulong record, ulong parent, string name, bool directory = false) =>
        new(Reference(record), parent, name, directory ? FileAttributes.Directory : FileAttributes.Archive, directory ? 0 : 1024,
            DateTime.UtcNow.Ticks);

    private sealed class Locations : IIndexedLocationProvider
    {
        public IndexLocations Resolve() => new(@"C:\Users\test\Desktop", [@"C:\"]);
    }

    private sealed class PrefetchChannel : IChangeFeedRequestChannel
    {
        private readonly FakeVolumeChannel _inner = new();
        internal readonly TaskCompletionSource NextRequested = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal readonly TaskCompletionSource ReleaseNext = new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken cancellationToken)
        {
            if (request.Kind == ChangeFeedRequestKind.IndexVolumeScan && request.Token is not null)
            {
                NextRequested.TrySetResult();
                await ReleaseNext.Task.WaitAsync(cancellationToken);
            }
            return await _inner.SendAsync(request, cancellationToken);
        }
    }

    private sealed class FakeVolumeChannel : IChangeFeedRequestChannel
    {
        internal readonly List<IndexEntry> Entries =
        [
            new(Root, Root, ".", FileAttributes.Directory, 0, 0),
            Entry(20, Root, "Belgeler", directory: true),
            Entry(21, Reference(20), "rapor.pdf"),
            Entry(22, Reference(20), "notlar.txt"),
            Entry(23, Root, "kurulum.exe")
        ];
        internal readonly Queue<List<IndexRecordState>> Pending = new();
        internal ulong JournalId = 9;
        internal long NextUsn = 100;
        internal long LastRequestedUsn;
        internal int Scans;
        internal readonly List<IndexBuildOptions?> ScanOptions = [];

        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken cancellationToken)
        {
            IndexVolumePageDto page;
            switch (request.Kind)
            {
                case ChangeFeedRequestKind.IndexVolumeOpen:
                    page = new(@"C:\", Root, 0xABCD, JournalId, 0, NextUsn);
                    break;
                case ChangeFeedRequestKind.IndexVolumeScan when request.Token is null:
                    Scans++;
                    ScanOptions.Add(request.IndexBuild);
                    page = new(@"C:\", 0, 0, 0, 0, 0, "next", IndexEntryCodec.PackEntries(Entries.Take(2)), Count: 2);
                    break;
                case ChangeFeedRequestKind.IndexVolumeScan:
                    Assert.Null(request.IndexBuild);
                    page = new(@"C:\", 0, 0, 0, 0, 0, null, IndexEntryCodec.PackEntries(Entries.Skip(2)), Count: Entries.Count - 2, Done: true);
                    break;
                case ChangeFeedRequestKind.IndexVolumeChanges:
                    LastRequestedUsn = request.Journal!.NextUsn;
                    if (request.Journal.JournalId != JournalId)
                    {
                        page = new(@"C:\", 0, 0, JournalId, 0, NextUsn, RescanRequired: true);
                        break;
                    }
                    if (Pending.TryDequeue(out var states))
                    {
                        NextUsn += 10;
                        page = new(@"C:\", 0, 0, JournalId, 0, NextUsn, States: IndexEntryCodec.PackStates(states), Count: states.Count, Done: true);
                    }
                    else page = new(@"C:\", 0, 0, JournalId, 0, NextUsn, Done: true);
                    break;
                default:
                    throw new InvalidOperationException(request.Kind.ToString());
            }
            return Task.FromResult(new ChangeFeedResponse(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok, Volume: page));
        }
    }
}
