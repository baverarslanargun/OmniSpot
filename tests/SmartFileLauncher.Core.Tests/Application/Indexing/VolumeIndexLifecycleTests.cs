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
    public async Task BinaryPagesPreserveSearchAndSnapshotResults()
    {
        using var directory = new TemporaryDirectory();
        var channel = new FakeVolumeChannel { Binary = true };
        using (var service = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path))
        {
            var started = await service.InitializeAsync();
            Assert.Equal(3, started.Stats.FileCount);
            Assert.Equal(["rapor.pdf"], Search(service, "rapor"));
        }
        using var restored = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path);
        await restored.InitializeAsync();
        Assert.Equal(1, channel.Scans);
        Assert.Equal(["rapor.pdf"], Search(restored, "rapor"));
    }

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
        var sent = options with { SortMethod = IndexSortMethod.Pivot };
        using var service = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path, options);
        await service.InitializeAsync();
        Assert.Equal(sent, Assert.Single(channel.ScanOptions));
        channel.JournalId = 99;
        await service.EnsureSyncedAsync(@"C:\");
        Assert.Equal(new[] { sent, sent }, channel.ScanOptions);
    }

    [Theory]
    [InlineData(IndexSortMethod.Ps5, "pS⁵")]
    [InlineData(IndexSortMethod.Pivot, "Pivot")]
    [InlineData(IndexSortMethod.Merge, "Birleştir")]
    public async Task SelectedSortMethodAndWorkersReachTheNameSortOfANewScan(IndexSortMethod method, string name)
    {
        using var directory = new TemporaryDirectory();
        var channel = new FakeVolumeChannel();
        var statuses = new List<string>();
        using var service = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path,
            new IndexBuildOptions(SortMethod: method, AdaptiveSort: false, SortWorkers: 3));
        service.ProgressChanged += progress => statuses.Add(progress.Status);
        await service.InitializeAsync();
        Assert.Contains(statuses, status => status.StartsWith($"{name} ad sıralaması:", StringComparison.Ordinal) &&
            status.EndsWith("en çok 3 işçi", StringComparison.Ordinal));
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
    public async Task APreloadedSnapshotIsUsedWithoutScanningAndAcceptsLaterChanges()
    {
        using var directory = new TemporaryDirectory();
        var channel = new FakeVolumeChannel();
        using (var first = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path))
            await first.InitializeAsync();
        using var second = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path);
        second.Preload();
        second.Preload();
        await second.InitializeAsync();
        second.Preload();
        Assert.Equal(1, channel.Scans);
        Assert.Equal(["rapor.pdf"], Search(second, "rapor"));
        channel.Pending.Enqueue([new IndexRecordState(Reference(30), [Entry(30, Reference(20), "yeni-belge.docx")])]);
        Assert.True(await second.EnsureSyncedAsync(@"C:\"));
        Assert.Equal(["yeni-belge.docx"], Search(second, "yeni"));
    }

    [Fact]
    public async Task APreloadIsIgnoredWhenARebuildIsRequestedOrTheSnapshotIsDamaged()
    {
        using var directory = new TemporaryDirectory();
        var channel = new FakeVolumeChannel();
        string snapshot;
        using (var first = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path))
        {
            await first.InitializeAsync();
            snapshot = Assert.Single(File.ReadAllLines(first.DatabasePath));
        }
        File.WriteAllText(Path.Combine(directory.Path, "rebuild.request"), "");
        using (var rebuilt = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path))
        {
            rebuilt.Preload();
            await rebuilt.InitializeAsync();
            Assert.Equal(2, channel.Scans);
        }
        var bytes = File.ReadAllBytes(snapshot);
        bytes[bytes.Length / 2] ^= 0x5A;
        File.WriteAllBytes(snapshot, bytes);
        using var recovered = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path);
        recovered.Preload();
        await recovered.InitializeAsync();
        Assert.Equal(3, channel.Scans);
        Assert.Equal(["rapor.pdf"], Search(recovered, "rapor"));
    }

    [Fact]
    public async Task APreloadForAVolumeThatIsNoLongerSelectedIsDiscarded()
    {
        using var directory = new TemporaryDirectory();
        var channel = new MultipleVolumeChannel();
        using (var first = new VolumeIndexLifecycleService(channel, new Locations([@"C:\", @"D:\"]), directory.Path))
            await first.InitializeAsync();
        using (var unused = new VolumeIndexLifecycleService(channel, new Locations([@"C:\"]), directory.Path))
            unused.Preload();
        using var second = new VolumeIndexLifecycleService(channel, new Locations([@"C:\"]), directory.Path);
        second.Preload();
        await second.InitializeAsync();
        Assert.Equal(2, channel.Scans);
        Assert.Equal(["rapor.pdf"], Search(second, "rapor"));
        Assert.Equal(Path.Combine(directory.Path, "000000000000ABCD.idx"), Assert.Single(File.ReadAllLines(second.DatabasePath)));
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

    [Fact]
    public async Task RemovedVolumeSnapshotsAreDeletedAfterTheRemainingVolumeLoads()
    {
        using var directory = new TemporaryDirectory();
        var channel = new MultipleVolumeChannel();
        using (var first = new VolumeIndexLifecycleService(channel, new Locations([@"C:\", @"D:\"]), directory.Path,
            removeUnusedSnapshots: true))
        {
            await first.InitializeAsync();
            Assert.Equal(2, File.ReadAllLines(first.DatabasePath).Length);
        }
        var active = Path.Combine(directory.Path, "000000000000ABCD.idx");
        var removed = Path.Combine(directory.Path, "000000000000DCBA.idx");
        var original = File.ReadAllBytes(active);
        var preserved = new[] { active + ".tmp", Path.Combine(directory.Path, "notes.idx"),
            Path.Combine(directory.Path, "000000000000DCBA.idx.bak"), Path.Combine(directory.Path, "GGGGGGGGGGGGGGGG.idx"),
            Path.Combine(directory.Path, "other", "000000000000DCBA.idx") };
        Directory.CreateDirectory(Path.Combine(directory.Path, "other"));
        foreach (var path in preserved) File.WriteAllText(path, "keep");
        File.WriteAllText(removed + ".tmp", "partial snapshot");
        using var second = new VolumeIndexLifecycleService(channel, new Locations([@"C:\Belgeler"]), directory.Path,
            removeUnusedSnapshots: true);

        await second.InitializeAsync();

        Assert.False(File.Exists(removed));
        Assert.False(File.Exists(removed + ".tmp"));
        Assert.Equal(original, File.ReadAllBytes(active));
        Assert.All(preserved, path => Assert.Equal("keep", File.ReadAllText(path)));
        Assert.Equal(active, Assert.Single(File.ReadAllLines(second.DatabasePath)));
        using var loaded = FileIndex.Load(active, out _);
        Assert.NotNull(loaded);
        Assert.Equal(["rapor.pdf"], loaded.Search("rapor", 50, ResultView.SearchDefault).Select(result => result.Name));
        Assert.Equal(2, channel.Scans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AnUnavailableOrCancelledSelectedVolumeDoesNotRemoveSnapshots(bool cancelled)
    {
        using var directory = new TemporaryDirectory();
        var channel = new MultipleVolumeChannel();
        using (var first = new VolumeIndexLifecycleService(channel, new Locations([@"C:\", @"D:\"]), directory.Path,
            removeUnusedSnapshots: true))
            await first.InitializeAsync();
        var before = Directory.GetFiles(directory.Path).ToDictionary(path => path, File.ReadAllBytes);
        channel.FailRoot = @"D:\";
        channel.CancelOpen = cancelled;
        using var second = new VolumeIndexLifecycleService(channel, new Locations([@"C:\", @"D:\"]), directory.Path,
            removeUnusedSnapshots: true);

        if (cancelled)
        {
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second.InitializeAsync());
            Assert.False(second.IsInitialized);
        }
        else
        {
            await second.InitializeAsync();
            Assert.True(second.IsInitialized);
            Assert.Equal(["rapor.pdf"], Search(second, "rapor"));
        }

        Assert.All(before, pair => Assert.Equal(pair.Value, File.ReadAllBytes(pair.Key)));
    }

    [Fact]
    public async Task WhenEveryVolumeFailsStartupReportsTheIssueOfTheFirstVolume()
    {
        using var directory = new TemporaryDirectory();
        var channel = new MultipleVolumeChannel { FailRoot = @"D:\", OpenFailure = JournalUnavailable(structured: true) };
        using var service = new VolumeIndexLifecycleService(channel, new Locations([@"D:\"]), directory.Path);

        var failure = await Assert.ThrowsAsync<IndexIssueException>(() => service.InitializeAsync());

        Assert.Equal(IndexIssueReason.JournalInactive, failure.Issue.Reason);
        Assert.Equal(@"D:\", failure.Issue.Location);
        Assert.False(failure.Issue.LocationSkipped);
        Assert.False(service.IsInitialized);
    }

    [Fact]
    public async Task ASkippedVolumeIsAddedOnceItOpens()
    {
        using var directory = new TemporaryDirectory();
        var channel = new MultipleVolumeChannel { FailRoot = @"D:\" };
        using var service = new VolumeIndexLifecycleService(channel, new Locations([@"C:\", @"D:\"]), directory.Path)
        {
            RetryInterval = TimeSpan.FromMilliseconds(200)
        };
        var added = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var announced = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        service.Notice += notice => { if (notice == @"D:\ indekse eklendi.") added.TrySetResult(); };
        service.FileChanged += _ => { if (added.Task.IsCompleted) announced.TrySetResult(); };
        await service.InitializeAsync();
        Assert.Single(Search(service, "rapor"));
        Assert.False(await service.EnsureSyncedAsync(@"D:\"));

        channel.FailRoot = null;
        await added.Task.WaitAsync(TimeSpan.FromSeconds(15));

        Assert.Equal(2, Search(service, "rapor").Length);
        Assert.True(await service.EnsureSyncedAsync(@"D:\"));
        await announced.Task.WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(2, File.ReadAllLines(service.DatabasePath).Length);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task TemporaryOrEmptyScopesDoNotRemoveUnusedSnapshots(bool cleanup, bool empty)
    {
        using var directory = new TemporaryDirectory();
        var orphan = Path.Combine(directory.Path, "000000000000DCBA.idx");
        File.WriteAllText(orphan, "keep");
        using var service = new VolumeIndexLifecycleService(new FakeVolumeChannel(),
            new Locations(empty ? [] : [@"C:\"]), directory.Path, removeUnusedSnapshots: cleanup);

        await service.InitializeAsync();

        Assert.Equal("keep", File.ReadAllText(orphan));
    }

    [Fact]
    public async Task ALockedUnusedSnapshotDoesNotPreventStartupAndIsRetriedOnNextStartup()
    {
        using var directory = new TemporaryDirectory();
        var orphan = Path.Combine(directory.Path, "000000000000DCBA.idx");
        File.WriteAllText(orphan, "locked");
        var channel = new FakeVolumeChannel();
        var notices = new List<string>();
        using (var locked = File.Open(orphan, FileMode.Open, FileAccess.Read, FileShare.None))
        using (var first = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path, removeUnusedSnapshots: true))
        {
            first.Notice += notices.Add;
            await first.InitializeAsync();
            Assert.True(first.IsInitialized);
            Assert.True(File.Exists(orphan));
            Assert.Contains(notices, notice => notice.Contains("Eski indeks dosyası temizlenemedi", StringComparison.Ordinal));
            Assert.Equal(["rapor.pdf"], Search(first, "rapor"));
        }
        using var second = new VolumeIndexLifecycleService(channel, new Locations(), directory.Path, removeUnusedSnapshots: true);
        await second.InitializeAsync();
        Assert.False(File.Exists(orphan));
        Assert.Equal(1, channel.Scans);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task InactiveJournalSkipsOnlyThatVolumeAndPreservesTheRebuildRequest(bool structured)
    {
        using var directory = new TemporaryDirectory();
        var marker = Path.Combine(directory.Path, "rebuild.request");
        File.WriteAllText(marker, "");
        var channel = new MultipleVolumeChannel { FailRoot = @"D:\", OpenFailure = JournalUnavailable(structured) };
        using var service = new VolumeIndexLifecycleService(channel, new Locations([@"C:\", @"D:\"]), directory.Path);
        IndexHealth? first = null;
        service.HealthChanged += health => Interlocked.CompareExchange(ref first, health, null);

        await service.InitializeAsync();

        Assert.True(service.IsInitialized);
        Assert.Equal(["rapor.pdf"], Search(service, "rapor"));
        Assert.NotNull(first);
        Assert.True(first.SearchAvailable);
        var issue = Assert.Single(first.Issues);
        Assert.Equal(IndexIssueReason.JournalInactive, issue.Reason);
        Assert.Equal(@"D:\", issue.Location);
        Assert.Equal(1179, issue.NativeErrorCode);
        Assert.Equal("FSCTL_QUERY_USN_JOURNAL başarısız: 1179", issue.Detail);
        Assert.True(issue.LocationSkipped);
        Assert.True(File.Exists(marker));
        Assert.False(await service.EnsureSyncedAsync(@"D:\"));
        Assert.True(await service.EnsureSyncedAsync(@"C:\"));
    }

    [Fact]
    public async Task BackgroundFailureKeepsResultsAndClearsAfterTheVolumeRecovers()
    {
        using var directory = new TemporaryDirectory();
        var channel = new MultipleVolumeChannel();
        using var service = new VolumeIndexLifecycleService(channel, new Locations([@"D:\", @"C:\"]), directory.Path);
        var updates = System.Threading.Channels.Channel.CreateUnbounded<IndexHealth>();
        service.HealthChanged += health => updates.Writer.TryWrite(health);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await service.InitializeAsync(deadline.Token);
        channel.FailChanges = true;

        var degraded = await updates.Reader.ReadAsync(deadline.Token);
        Assert.True(degraded.SearchAvailable);
        Assert.Equal(@"D:\", Assert.Single(degraded.Issues).Location);
        Assert.Equal(IndexIssueReason.JournalInactive, degraded.Issues[0].Reason);
        Assert.Equal(2, Search(service, "rapor").Length);
        Assert.True(await service.EnsureSyncedAsync(@"C:\", deadline.Token));
        Assert.False(await service.EnsureSyncedAsync(@"D:\", deadline.Token));
        Assert.False(updates.Reader.TryRead(out _));

        channel.FailChanges = false;
        var recovered = await updates.Reader.ReadAsync(deadline.Token);
        Assert.True(recovered.SearchAvailable);
        Assert.Empty(recovered.Issues);
    }

    private static ChangeFeedResponse JournalUnavailable(bool structured)
    {
        var failure = SmartFileLauncher.Core.ChangeFeed.Usn.UsnVolumeJournalReader.TranslateFailure(1179, "FSCTL_QUERY_USN_JOURNAL");
        var response = ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, failure.Message);
        return structured ? response with
        {
            IndexIssue = IndexIssue.FromException(failure, @"D:\", ChangeFeedRequestKind.IndexVolumeOpen.ToString())
        } : response;
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

    private sealed class Locations(IReadOnlyList<string>? roots = null) : IIndexedLocationProvider
    {
        public IndexLocations Resolve() => new(@"C:\Users\test\Desktop", roots ?? [@"C:\"]);
    }

    private sealed class MultipleVolumeChannel : IChangeFeedRequestChannel
    {
        private readonly Dictionary<string, FakeVolumeChannel> _channels = new(StringComparer.OrdinalIgnoreCase);
        internal string? FailRoot;
        internal bool CancelOpen;
        internal ChangeFeedResponse? OpenFailure;
        internal volatile bool FailChanges;
        internal int Scans => _channels.Values.Sum(channel => channel.Scans);

        public async Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken cancellationToken)
        {
            if (request.Kind == ChangeFeedRequestKind.IndexVolumeOpen && request.RootPath == FailRoot)
            {
                if (CancelOpen) throw new OperationCanceledException();
                if (OpenFailure is not null) return OpenFailure;
                throw new IOException("Birim erişilemiyor.");
            }
            if (request.Kind == ChangeFeedRequestKind.IndexVolumeChanges && request.RootPath == @"D:\" && FailChanges)
                return JournalUnavailable(structured: true);
            var volume = Path.GetPathRoot(request.RootPath!)!;
            if (!_channels.TryGetValue(volume, out var channel))
                _channels.Add(volume, channel = new FakeVolumeChannel());
            var response = await channel.SendAsync(request, cancellationToken);
            return response with { Volume = response.Volume! with
            {
                RootPath = volume,
                VolumeSerial = volume == @"C:\" ? 0xABCDUL : 0xDCBAUL
            } };
        }
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
        internal bool Binary;
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
            if (request.Kind == ChangeFeedRequestKind.IndexVolumeScan)
            {
                Assert.True(request.BinaryVolumeEntries);
                if (Binary)
                {
                    var buffer = new System.Buffers.ArrayBufferWriter<byte>();
                    foreach (var entry in IndexEntryCodec.UnpackEntries(page.Entries)) IndexEntryCodec.WriteBinary(buffer, entry);
                    page = page with { Entries = null, BinaryEntries = buffer.WrittenMemory };
                }
            }
            return Task.FromResult(new ChangeFeedResponse(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok, Volume: page));
        }
    }
}
