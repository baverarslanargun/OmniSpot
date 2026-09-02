using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnDrainRunnerTests : IDisposable
{
    private const string OwnerSid = "S-1-5-21-9-9-9-1001";
    private const ulong JournalId = 7;
    private const long BootstrapUsn = 1000;

    private readonly TemporaryDirectory _storeRoot = new();
    private readonly TemporaryDirectory _firstRoot = new();
    private readonly TemporaryDirectory _secondRoot = new();
    private readonly UsnFileSystemIdentityProbe _probe = new();

    public void Dispose()
    {
        _storeRoot.Dispose();
        _firstRoot.Dispose();
        _secondRoot.Dispose();
    }

    [Fact]
    public void Run_ReportsNoSubscriptionWhenNothingIsRegistered()
    {
        var store = CreateStore();
        var result = CreateRunner(store, CreateReader()).Run();

        Assert.Equal(UsnDrainOutcome.NoSubscription, result.Outcome);
        Assert.Empty(store.ReadPending());
    }

    [Fact]
    public void Run_BootstrapsAnUnknownRootAndAnnouncesItAsNotYetSynchronized()
    {
        _firstRoot.CreateDirectory("alt");
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);

        var result = CreateRunner(store, CreateReader()).Run();

        Assert.Equal(UsnDrainOutcome.Completed, result.Outcome);
        Assert.Equal(1, result.RootsGapped);

        var delivery = Assert.Single(Assert.Single(store.ReadPending()).Roots);
        Assert.Equal(_firstRoot.Path, delivery.RootPath);
        Assert.Equal(ChangeFeedGapReason.NotYetSynchronized, delivery.Batch.GapReason);

        var state = Assert.Single(ReadState()!.Roots);
        Assert.Equal(BootstrapUsn, state.SynchronizedFromUsn);
        Assert.Equal("alt", Assert.Single(state.Directories).Name);
    }

    [Fact]
    public void Run_SkipsRecordsWrittenBeforeTheRootWasSynchronized()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(BootstrapUsn - 10, 50, RootReference(_firstRoot), UsnReason.FileCreate, "eski.txt")
                .AddVersion2(BootstrapUsn + 10, 51, RootReference(_firstRoot), UsnReason.FileCreate, "yeni.txt")
                .Build());

        CreateRunner(store, reader).Run();

        var events = Assert.Single(Assert.Single(store.ReadPending()).Roots).Batch.Events;
        Assert.Equal(
            Path.Combine(_firstRoot.Path, "yeni.txt"),
            Assert.Single(events).FullPath);
    }

    [Fact]
    public void Run_WritesEventsIntoTheQueueAndAdvancesTheCursor()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, RootReference(_firstRoot), UsnReason.FileCreate, "rapor.txt")
                .Build());

        var result = CreateRunner(store, reader).Run();

        Assert.Equal(1, result.EntriesWritten);
        Assert.Equal(1, result.EventsWritten);

        var entry = Assert.Single(store.ReadPending());
        Assert.Equal(BootstrapUsn, entry.FromUsn);
        Assert.Equal(2000, entry.ToUsn);
        Assert.Equal(2000, ReadState()!.NextUsn);
    }

    [Fact]
    public void Run_WritesNothingWhenNoRecordTouchesTheRoot()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, 999999, UsnReason.FileCreate, "baska.txt")
                .Build());

        var result = CreateRunner(store, reader).Run();

        Assert.Equal(0, result.EntriesWritten);
        Assert.Empty(store.ReadPending());
        Assert.Equal(2000, ReadState()!.NextUsn);
    }

    [Fact]
    public void Run_DiscardsAnEntryLeftBehindByACrashedDrain()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        var orphan = store.Enqueue(
            VolumeId(_firstRoot),
            JournalId,
            BootstrapUsn,
            5000,
            new[]
            {
                new ChangeFeedRootDelivery(
                    _firstRoot.Path,
                    ChangeFeedBatch.Ok(new[]
                    {
                        new ChangeFeedEvent(ChangeFeedEventKind.Created, "yetim", false)
                    }))
            });

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(2000, Array.Empty<byte>());

        CreateRunner(store, reader).Run();

        Assert.DoesNotContain(
            orphan.Sequence,
            store.ReadPending().Select(entry => entry.Sequence));
    }

    [Fact]
    public void Run_GapsOnlyTheRootThatDisappearedAndKeepsTheOtherRunning()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path, _secondRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        Directory.Delete(_secondRoot.Path, recursive: true);

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, RootReference(_firstRoot), UsnReason.FileCreate, "rapor.txt")
                .Build());

        CreateRunner(store, reader).Run();

        var roots = Assert.Single(store.ReadPending()).Roots;
        var healthy = roots.Single(root => root.RootPath == _firstRoot.Path);
        var missing = roots.Single(root => root.RootPath == _secondRoot.Path);

        Assert.Equal(ChangeFeedStatus.Ok, healthy.Batch.Status);
        Assert.Single(healthy.Batch.Events);
        Assert.Equal(ChangeFeedGapReason.RootUnavailable, missing.Batch.GapReason);
        Assert.Equal(_firstRoot.Path, Assert.Single(ReadState()!.Roots).RootPath);
    }

    [Fact]
    public void Run_ReadsTheJournalOnceForRootsThatShareAVolume()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path, _secondRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(2000, Array.Empty<byte>());
        CreateRunner(store, reader).Run();

        Assert.Single(reader.ReadCalls);
    }

    [Fact]
    public void Run_ReportsAFaultAndADurableGapWhenTheVolumeCannotBeOpened()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);

        var result = new UsnDrainRunner(
            Layout(),
            store,
            new ThrowingReaderFactory(),
            _probe).Run();

        Assert.Equal(UsnDrainOutcome.Faulted, result.Outcome);
        Assert.Equal(1, result.VolumesFaulted);
        Assert.NotNull(result.Diagnostics);

        var delivery = Assert.Single(Assert.Single(store.ReadPending()).Roots);
        Assert.Equal(_firstRoot.Path, delivery.RootPath);
        Assert.Equal(ChangeFeedGapReason.JournalUnavailable, delivery.Batch.GapReason);
    }

    [Fact]
    public void Run_RejectsASubscriptionOwnedByAnotherAccount()
    {
        var store = CreateStore();
        SubscribeAs(store, "S-1-5-21-9-9-9-2002", _firstRoot.Path);

        var result = CreateRunner(store, CreateReader()).Run();

        Assert.Equal(UsnDrainOutcome.SubscriptionRejected, result.Outcome);
        Assert.Empty(store.ReadPending());
        Assert.Null(ReadState());
    }

    [Fact]
    public void Run_GapsARootWhoseIdentityNoLongerMatchesTheSubscription()
    {
        var store = CreateStore();
        store.WriteSubscription(new ChangeFeedSubscription(
            OwnerSid,
            new[]
            {
                new ChangeFeedSubscribedRoot(
                    _firstRoot.Path,
                    new ChangeFeedRootIdentity("ntfs-vsn:0x0000000000000001", "0x0000000000000002"))
            }));

        var result = CreateRunner(store, CreateReader()).Run();

        var delivery = Assert.Single(Assert.Single(store.ReadPending()).Roots);
        Assert.Equal(ChangeFeedGapReason.RootIdentityChanged, delivery.Batch.GapReason);
        Assert.Equal(1, result.RootsGapped);
        Assert.Null(ReadState());
    }

    [Fact]
    public void Run_ResumesARootWhoseFloorIsAheadOfTheVolumeCursor()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        WriteState(nextUsn: 500, synchronizedFromUsn: 900);

        var reader = CreateReader().EnqueuePage(
            BootstrapUsn,
            new UsnRecordBuffer()
                .AddVersion2(800, 50, RootReference(_firstRoot), UsnReason.FileCreate, "erken.txt")
                .AddVersion2(950, 51, RootReference(_firstRoot), UsnReason.FileCreate, "gec.txt")
                .Build());

        CreateRunner(store, reader).Run();

        var delivery = Assert.Single(Assert.Single(store.ReadPending()).Roots);
        Assert.Equal(ChangeFeedStatus.Ok, delivery.Batch.Status);
        Assert.Equal(
            Path.Combine(_firstRoot.Path, "gec.txt"),
            Assert.Single(delivery.Batch.Events).FullPath);
    }

    [Fact]
    public void Run_RebuildsARootWhoseFloorIsBeyondTheJournalHead()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        WriteState(nextUsn: 500, synchronizedFromUsn: BootstrapUsn + 5000);

        CreateRunner(store, CreateReader()).Run();

        var delivery = Assert.Single(Assert.Single(store.ReadPending()).Roots);
        Assert.Equal(ChangeFeedGapReason.NotYetSynchronized, delivery.Batch.GapReason);
        Assert.Equal(BootstrapUsn, Assert.Single(ReadState()!.Roots).SynchronizedFromUsn);
    }

    private void WriteState(long nextUsn, long synchronizedFromUsn)
    {
        Assert.True(_probe.TryReadIdentity(_firstRoot.Path, out var identity));

        var volumeRoot = UsnVolumeJournalReader.ResolveVolumeRoot(_firstRoot.Path);
        var key = new string(volumeRoot
            .TrimEnd(Path.DirectorySeparatorChar)
            .Select(character => char.IsLetterOrDigit(character) ? char.ToUpperInvariant(character) : '_')
            .ToArray());

        new UsnChangeFeedStateStore(Path.Combine(Layout().StateDirectory, key + ".json")).Write(
            JournalId,
            nextUsn,
            new[]
            {
                new UsnChangeFeedState(
                    _firstRoot.Path,
                    identity,
                    JournalId,
                    nextUsn,
                    Array.Empty<UsnDirectoryEntry>(),
                    synchronizedFromUsn)
            });
    }

    private string VolumeId(TemporaryDirectory root)
    {
        Assert.True(_probe.TryReadIdentity(root.Path, out var identity));
        return identity.ToChangeFeedRootIdentity().VolumeId;
    }

    private ulong RootReference(TemporaryDirectory root)
    {
        Assert.True(_probe.TryReadIdentity(root.Path, out var identity));
        Assert.Equal(0ul, identity.FileReference.High);
        return identity.FileReference.Low;
    }

    private ChangeFeedStoreLayout Layout() =>
        ChangeFeedStoreLayout.ForOwner(_storeRoot.Path, OwnerSid);

    private FileSystemChangeFeedStore CreateStore() => new(Layout());

    private UsnDrainRunner CreateRunner(
        IChangeFeedStore store,
        FakeUsnJournalReader reader) =>
        new(Layout(), store, new SingleReaderFactory(reader), _probe, new FakeUsnSubtreeReader());

    private UsnVolumeFeedState? ReadState()
    {
        var file = Directory.GetFiles(Layout().StateDirectory, "*.json").SingleOrDefault();
        return file is null ? null : new UsnChangeFeedStateStore(file).Read();
    }

    private void Subscribe(IChangeFeedStore store, params string[] roots) =>
        SubscribeAs(store, OwnerSid, roots);

    private void SubscribeAs(IChangeFeedStore store, string ownerSid, params string[] roots)
    {
        store.WriteSubscription(new ChangeFeedSubscription(
            ownerSid,
            roots.Select(SubscribedRoot).ToArray()));
    }

    private ChangeFeedSubscribedRoot SubscribedRoot(string path)
    {
        Assert.True(_probe.TryReadIdentity(path, out var identity));
        return new ChangeFeedSubscribedRoot(path, identity.ToChangeFeedRootIdentity());
    }

    private static UsnJournalDescriptor Descriptor(long nextUsn = BootstrapUsn) =>
        new(JournalId, 50, nextUsn, 0, long.MaxValue, 0, 0);

    private static FakeUsnJournalReader CreateReader() =>
        new() { Descriptor = Descriptor() };

    private sealed class SingleReaderFactory : IUsnJournalReaderFactory
    {
        private readonly IUsnJournalReader _reader;

        public SingleReaderFactory(IUsnJournalReader reader)
        {
            _reader = reader;
        }

        public IUsnJournalReader Open(string volumeRootPath) => _reader;
    }

    private sealed class ThrowingReaderFactory : IUsnJournalReaderFactory
    {
        public IUsnJournalReader Open(string volumeRootPath) =>
            throw new UnauthorizedAccessException("Test: birim açılamadı.");
    }
}
