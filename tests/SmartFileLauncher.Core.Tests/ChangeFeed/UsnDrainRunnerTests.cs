using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class UsnDrainRunnerTests : IDisposable
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
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
        Assert.Empty(store.ReadPending().Entries);
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

        var delivery = Assert.Single(Assert.Single(store.ReadPending().Entries).Roots);
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

        var events = Assert.Single(Assert.Single(store.ReadPending().Entries).Roots).Batch.Events;
        Assert.Equal(
            Path.Combine(_firstRoot.Path, "yeni.txt"),
            Assert.Single(events).FullPath);
    }

    [Fact]
    public void AHeldLease_StillReadsTheJournalButDeliversNothing()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        store.HoldLease(TimeSpan.FromMinutes(5));

        var factory = new CountingReaderFactory(CreateReader());
        var result = new UsnDrainRunner(
            Layout(),
            store,
            factory,
            _probe,
            new FakeUsnSubtreeReader()).Run();

        Assert.Equal(UsnDrainOutcome.LeaseHeld, result.Outcome);
        Assert.Equal(1, factory.OpenCount);
        Assert.Empty(store.ReadPending().Entries);
        Assert.Equal(0, result.EntriesWritten);
        Assert.Equal(0, result.EventsWritten);
    }

    [Fact]
    public void AHeldLease_KeepsTheCursorMovingSoItCannotFallOutOfTheJournal()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);
        var before = ReadState();

        store.HoldLease(TimeSpan.FromMinutes(5));
        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, RootReference(_firstRoot), UsnReason.FileCreate, "rapor.txt")
                .Build());

        var result = CreateRunner(store, reader).Run();

        Assert.Equal(UsnDrainOutcome.LeaseHeld, result.Outcome);
        Assert.Empty(store.ReadPending().Entries);
        Assert.True(
            ReadState()!.NextUsn > before!.NextUsn,
            "Kira tutulurken imleç donarsa uzun bir oturumda günlük imlecin üstünden " +
            "geçer ve servis yerini tamamen kaybeder. Teslimat kapalı, konum takibi açık: " +
            $"önce {before.NextUsn}, sonra {ReadState()!.NextUsn}");
    }

    [Fact]
    public void ASecurityChangeWhileFollowing_DoesNotFreezeTheCursor()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);
        var before = ReadState();

        store.HoldLease(TimeSpan.FromMinutes(5));
        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, RootReference(_firstRoot), UsnReason.SecurityChange, "rapor.txt")
                .Build());

        CreateRunner(store, reader).Run();

        Assert.True(
            ReadState()!.NextUsn > before!.NextUsn,
            "Güvenlik değişimi takip turunu durdurursa aynı kayıtlar her turda yeniden " +
            "okunur, bayrak kalıcı olarak doğru kalır ve imleç bir daha asla ilerlemez: " +
            $"önce {before.NextUsn}, sonra {ReadState()!.NextUsn}");
    }

    [Fact]
    public void ASecurityChangeWhileFollowing_IsHeldBackUntilTheServiceDrainsAgain()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);
        var stamp = store.ReadSecurityStamp();

        store.HoldLease(TimeSpan.FromMinutes(5));
        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, RootReference(_firstRoot), UsnReason.SecurityChange, "rapor.txt")
                .Build());

        CreateRunner(store, reader).Run();

        Assert.True(
            store.ReadSecurityStamp().Matches(stamp),
            "Damga kirayı tutanın devam jetonunu bağlıyor; takip turunda tazelenirse " +
            "açılıştaki devralma çekişi tam ortasından kopar.");
        Assert.True(ReadState()!.PendingSecurityChange);

        store.ReleaseLease();
        reader.Descriptor = Descriptor(nextUsn: 2500);
        reader.EnqueuePage(2500, new UsnRecordBuffer().Build());
        CreateRunner(store, reader).Run();

        Assert.False(
            store.ReadSecurityStamp().Matches(stamp),
            "Takipte görülen güvenlik değişimi düşürülemez; servis teslimata döndüğü " +
            "ilk turda bildirilmeli.");
        Assert.False(ReadState()!.PendingSecurityChange);
    }

    [Fact]
    public void AFollowRoundThatCannotAdvance_SaysWhy()
    {
        var inner = CreateStore();
        Subscribe(inner, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(inner, reader).Run();
        inner.Acknowledge(long.MaxValue);
        var before = ReadState();

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(2000, new UsnRecordBuffer().Build());

        var result = CreateRunner(new LeaseLostAfterFirstReadStore(inner), reader).Run();

        Assert.Equal(before!.NextUsn, ReadState()!.NextUsn);
        Assert.False(
            string.IsNullOrWhiteSpace(result.Diagnostics),
            "İlerlemeyen bir takip turu sessiz kalırsa donma günlükte hiç görünmez; " +
            "imlecin 43 dakika donduğu tam olarak bu yüzden fark edilmedi.");
    }

    [Fact]
    public void AnExpiredLease_LetsTheServiceTakeOver()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        store.HoldLease(TimeSpan.FromMinutes(1));

        var result = CreateRunner(
            store,
            CreateReader(),
            () => DateTime.UtcNow.AddMinutes(2)).Run();

        Assert.Equal(UsnDrainOutcome.Completed, result.Outcome);
        Assert.NotEmpty(store.ReadPending().Entries);
    }

    [Fact]
    public void AReleasedLease_LetsTheServiceTakeOverAtOnce()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        store.HoldLease(TimeSpan.FromMinutes(5));

        store.ReleaseLease();
        var result = CreateRunner(store, CreateReader()).Run();

        Assert.Equal(UsnDrainOutcome.Completed, result.Outcome);
        Assert.NotEmpty(store.ReadPending().Entries);
    }

    [Fact]
    public void ALeaseTakenMidRound_AbandonsTheWriteAndTheCursor()
    {
        var inner = CreateStore();
        Subscribe(inner, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(inner, reader).Run();
        inner.Acknowledge(long.MaxValue);
        var before = ReadState();

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, RootReference(_firstRoot), UsnReason.FileCreate, "rapor.txt")
                .Build());

        var store = new LeaseAfterFirstReadStore(inner);
        var result = CreateRunner(store, reader).Run();

        Assert.Equal(UsnDrainOutcome.LeasePreempted, result.Outcome);
        Assert.Equal(0, result.EntriesWritten);
        Assert.Empty(inner.ReadPending().Entries);
        Assert.Equal(before!.NextUsn, ReadState()!.NextUsn);
    }

    [Fact]
    public void ALeaseTakenBeforeAnEmptyRoundStillHoldsTheCursor()
    {
        var inner = CreateStore();
        Subscribe(inner, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(inner, reader).Run();
        inner.Acknowledge(long.MaxValue);
        var before = ReadState();

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, 4242, UsnReason.FileCreate, "baska.txt")
                .Build());

        var result = CreateRunner(new LeaseAfterFirstReadStore(inner), reader).Run();

        Assert.Equal(UsnDrainOutcome.LeasePreempted, result.Outcome);
        Assert.Equal(before!.NextUsn, ReadState()!.NextUsn);
    }

    [Fact]
    public void ALeaseTakenBeforeAGapAnnouncement_WritesNothing()
    {
        var store = CreateStore();
        var doomed = _firstRoot.CreateDirectory("gidecek");
        Subscribe(store, doomed);
        Directory.Delete(doomed, recursive: true);

        var result = CreateRunner(new LeaseAfterFirstReadStore(store), CreateReader()).Run();

        Assert.Equal(UsnDrainOutcome.LeasePreempted, result.Outcome);
        Assert.Empty(store.ReadPending().Entries);
    }

    [Fact]
    public async Task HoldLease_CannotLandWhileTheDrainIsCommitting()
    {
        var inner = CreateStore();
        Subscribe(inner, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(inner, reader).Run();
        inner.Acknowledge(long.MaxValue);

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, RootReference(_firstRoot), UsnReason.FileCreate, "rapor.txt")
                .Build());

        var order = new List<string>();
        using var committing = new ManualResetEventSlim();
        var store = new SlowCommitStore(inner, order, committing);

        var holder = Task.Run(() =>
        {
            Assert.True(committing.Wait(TimeSpan.FromSeconds(10)));
            CreateStore().HoldLease(TimeSpan.FromMinutes(5));

            lock (order)
            {
                order.Add("kira");
            }
        });

        var result = CreateRunner(store, reader).Run();
        await holder.WaitAsync(TimeSpan.FromSeconds(30));

        Assert.Equal(UsnDrainOutcome.Completed, result.Outcome);
        Assert.Equal(new[] { "yazma", "kira" }, order);
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

        var entry = Assert.Single(store.ReadPending().Entries);
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
        Assert.Empty(store.ReadPending().Entries);
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

        var orphan = store.EnqueueOne(
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
                    }),
                    ChangeFeedRootGeneration.New())
            });

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(2000, Array.Empty<byte>());

        CreateRunner(store, reader).Run();

        Assert.DoesNotContain(
            orphan.Sequence,
            store.ReadPending().Entries.Select(entry => entry.Sequence));
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

        var roots = Assert.Single(store.ReadPending().Entries).Roots;
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

        var delivery = Assert.Single(Assert.Single(store.ReadPending().Entries).Roots);
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
        Assert.Empty(store.ReadPending().Entries);
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
                    new ChangeFeedRootIdentity("ntfs-vsn:0x0000000000000001", "0x0000000000000002"),
                    ChangeFeedRootGeneration.New())
            }));

        var result = CreateRunner(store, CreateReader()).Run();

        var delivery = Assert.Single(Assert.Single(store.ReadPending().Entries).Roots);
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

        var delivery = Assert.Single(Assert.Single(store.ReadPending().Entries).Roots);
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

        var delivery = Assert.Single(Assert.Single(store.ReadPending().Entries).Roots);
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

    [Fact]
    public void Run_LeavesTheCursorUnacceptedWhenAnyBacklogPartFailsToLand()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        var before = ReadState();
        Assert.NotNull(before);

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(BootstrapUsn + 10, 51, RootReference(_firstRoot), UsnReason.FileCreate, "yeni.txt")
                .Build());

        var failing = new FailingEnqueueStore(store);
        try
        {
            CreateRunner(failing, reader).Run();
        }
        catch (IOException)
        {
        }

        var after = ReadState();
        Assert.NotNull(after);
        Assert.Equal(before!.NextUsn, after!.NextUsn);
        Assert.Empty(store.ReadPending().Entries);
    }

    [Fact]
    public void ASecurityChangeInTheJournal_StampsTheOwner()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        var before = store.ReadSecurityStamp();

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(
                    1500,
                    50,
                    RootReference(_firstRoot),
                    UsnReason.SecurityChange | UsnReason.Close,
                    "rapor.txt")
                .Build());

        CreateRunner(store, reader).Run();

        Assert.False(
            before.Matches(store.ReadSecurityStamp()),
            "İzin değişikliği sahibin güvenlik damgasını değiştirmedi.");
    }

    [Fact]
    public void ASecurityChangeAlongsideAGap_StillStampsTheOwner()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        var before = store.ReadSecurityStamp();

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(
                    1500,
                    50,
                    RootReference(_firstRoot),
                    UsnReason.SecurityChange | UsnReason.Close,
                    "rapor.txt")
                .AddVersion2(
                    1600,
                    RootReference(_firstRoot),
                    RootReference(_firstRoot),
                    UsnReason.FileDelete | UsnReason.Close,
                    "kok")
                .Build());

        var result = CreateRunner(store, reader).Run();

        Assert.True(result.RootsGapped > 0, "Kurulum bir boşluk üretmeliydi.");
        Assert.False(
            before.Matches(store.ReadSecurityStamp()),
            "Boşluk üreten bir turda izin değişikliği damgayı düşürdü.");
    }

    [Fact]
    public void AContentChangeInTheJournal_LeavesTheStampAlone()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        var before = store.ReadSecurityStamp();

        reader.Descriptor = Descriptor(nextUsn: 2000);
        reader.EnqueuePage(
            2000,
            new UsnRecordBuffer()
                .AddVersion2(1500, 50, RootReference(_firstRoot), UsnReason.FileCreate, "yeni.txt")
                .Build());

        CreateRunner(store, reader).Run();

        Assert.True(
            before.Matches(store.ReadSecurityStamp()),
            "Sıradan bir değişiklik güvenlik damgasını değiştirdi.");
    }

    private sealed class SlowCommitStore : IChangeFeedStore
    {
        private readonly IChangeFeedStore _inner;
        private readonly List<string> _order;
        private readonly ManualResetEventSlim _committing;

        public SlowCommitStore(
            IChangeFeedStore inner,
            List<string> order,
            ManualResetEventSlim committing)
        {
            _inner = inner;
            _order = order;
            _committing = committing;
        }

        public IReadOnlyList<ChangeFeedQueueEntry> Enqueue(
            string volumeId,
            ulong journalId,
            long fromUsn,
            long toUsn,
            IReadOnlyList<ChangeFeedRootDelivery> roots)
        {
            _committing.Set();
            Thread.Sleep(300);
            var written = _inner.Enqueue(volumeId, journalId, fromUsn, toUsn, roots);

            lock (_order)
            {
                _order.Add("yazma");
            }

            return written;
        }

        public IDisposable EnterOwnerScope(CancellationToken cancellationToken = default) =>
            _inner.EnterOwnerScope(cancellationToken);

        public ChangeFeedSubscription? ReadSubscription() => _inner.ReadSubscription();

        public void WriteSubscription(ChangeFeedSubscription subscription) =>
            _inner.WriteSubscription(subscription);

        public void DeleteSubscription() => _inner.DeleteSubscription();

        public ChangeFeedQueueEpoch ReadEpoch() => _inner.ReadEpoch();

        public ChangeFeedSecurityStamp ReadSecurityStamp() => _inner.ReadSecurityStamp();

        public void NoteSecurityChange() => _inner.NoteSecurityChange();

        public ChangeFeedWatcherLease ReadLease() => _inner.ReadLease();

        public ChangeFeedWatcherLease HoldLease(TimeSpan duration) =>
            _inner.HoldLease(duration);

        public void ReleaseLease() => _inner.ReleaseLease();

        public ChangeFeedQueueSlice ReadPending(ChangeFeedReadBudget? budget = null) =>
            _inner.ReadPending(budget);

        public void Acknowledge(long sequence) => _inner.Acknowledge(sequence);

        public int DiscardUncommitted(string volumeId, ulong journalId, long committedUsn) =>
            _inner.DiscardUncommitted(volumeId, journalId, committedUsn);
    }

    private sealed class LeaseAfterFirstReadStore : IChangeFeedStore
    {
        private readonly IChangeFeedStore _inner;
        private int _reads;

        public LeaseAfterFirstReadStore(IChangeFeedStore inner) => _inner = inner;

        public ChangeFeedWatcherLease ReadLease() =>
            ++_reads == 1
                ? ChangeFeedWatcherLease.None
                : ChangeFeedWatcherLease.Until(DateTime.UtcNow, TimeSpan.FromMinutes(5));

        public IDisposable EnterOwnerScope(CancellationToken cancellationToken = default) =>
            _inner.EnterOwnerScope(cancellationToken);

        public ChangeFeedSubscription? ReadSubscription() => _inner.ReadSubscription();

        public void WriteSubscription(ChangeFeedSubscription subscription) =>
            _inner.WriteSubscription(subscription);

        public void DeleteSubscription() => _inner.DeleteSubscription();

        public ChangeFeedQueueEpoch ReadEpoch() => _inner.ReadEpoch();

        public ChangeFeedSecurityStamp ReadSecurityStamp() => _inner.ReadSecurityStamp();

        public void NoteSecurityChange() => _inner.NoteSecurityChange();

        public ChangeFeedWatcherLease HoldLease(TimeSpan duration) =>
            _inner.HoldLease(duration);

        public void ReleaseLease() => _inner.ReleaseLease();

        public ChangeFeedQueueSlice ReadPending(ChangeFeedReadBudget? budget = null) =>
            _inner.ReadPending(budget);

        public IReadOnlyList<ChangeFeedQueueEntry> Enqueue(
            string volumeId,
            ulong journalId,
            long fromUsn,
            long toUsn,
            IReadOnlyList<ChangeFeedRootDelivery> roots) =>
            _inner.Enqueue(volumeId, journalId, fromUsn, toUsn, roots);

        public void Acknowledge(long sequence) => _inner.Acknowledge(sequence);

        public int DiscardUncommitted(string volumeId, ulong journalId, long committedUsn) =>
            _inner.DiscardUncommitted(volumeId, journalId, committedUsn);
    }

    private sealed class LeaseLostAfterFirstReadStore : IChangeFeedStore
    {
        private readonly IChangeFeedStore _inner;
        private int _reads;

        public LeaseLostAfterFirstReadStore(IChangeFeedStore inner) => _inner = inner;

        public ChangeFeedWatcherLease ReadLease() =>
            ++_reads == 1
                ? ChangeFeedWatcherLease.Until(DateTime.UtcNow, TimeSpan.FromMinutes(5))
                : ChangeFeedWatcherLease.None;

        public IDisposable EnterOwnerScope(CancellationToken cancellationToken = default) =>
            _inner.EnterOwnerScope(cancellationToken);

        public ChangeFeedSubscription? ReadSubscription() => _inner.ReadSubscription();

        public void WriteSubscription(ChangeFeedSubscription subscription) =>
            _inner.WriteSubscription(subscription);

        public void DeleteSubscription() => _inner.DeleteSubscription();

        public ChangeFeedQueueEpoch ReadEpoch() => _inner.ReadEpoch();

        public ChangeFeedSecurityStamp ReadSecurityStamp() => _inner.ReadSecurityStamp();

        public void NoteSecurityChange() => _inner.NoteSecurityChange();

        public ChangeFeedWatcherLease HoldLease(TimeSpan duration) =>
            _inner.HoldLease(duration);

        public void ReleaseLease() => _inner.ReleaseLease();

        public ChangeFeedQueueSlice ReadPending(ChangeFeedReadBudget? budget = null) =>
            _inner.ReadPending(budget);

        public IReadOnlyList<ChangeFeedQueueEntry> Enqueue(
            string volumeId,
            ulong journalId,
            long fromUsn,
            long toUsn,
            IReadOnlyList<ChangeFeedRootDelivery> roots) =>
            _inner.Enqueue(volumeId, journalId, fromUsn, toUsn, roots);

        public void Acknowledge(long sequence) => _inner.Acknowledge(sequence);

        public int DiscardUncommitted(string volumeId, ulong journalId, long committedUsn) =>
            _inner.DiscardUncommitted(volumeId, journalId, committedUsn);
    }

    private sealed class FailingEnqueueStore : IChangeFeedStore
    {
        private readonly IChangeFeedStore _inner;

        public FailingEnqueueStore(IChangeFeedStore inner) => _inner = inner;

        public IDisposable EnterOwnerScope(CancellationToken cancellationToken = default) =>
            _inner.EnterOwnerScope(cancellationToken);

        public ChangeFeedSubscription? ReadSubscription() => _inner.ReadSubscription();

        public void WriteSubscription(ChangeFeedSubscription subscription) =>
            _inner.WriteSubscription(subscription);

        public void DeleteSubscription() => _inner.DeleteSubscription();

        public ChangeFeedQueueEpoch ReadEpoch() => _inner.ReadEpoch();

        public ChangeFeedSecurityStamp ReadSecurityStamp() => _inner.ReadSecurityStamp();

        public void NoteSecurityChange() => _inner.NoteSecurityChange();

        public ChangeFeedWatcherLease ReadLease() => _inner.ReadLease();

        public ChangeFeedWatcherLease HoldLease(TimeSpan duration) =>
            _inner.HoldLease(duration);

        public void ReleaseLease() => _inner.ReleaseLease();

        public ChangeFeedQueueSlice ReadPending(ChangeFeedReadBudget? budget = null) =>
            _inner.ReadPending(budget);

        public IReadOnlyList<ChangeFeedQueueEntry> Enqueue(
            string volumeId,
            ulong journalId,
            long fromUsn,
            long toUsn,
            IReadOnlyList<ChangeFeedRootDelivery> roots) =>
            throw new IOException("Kuyruk yazılamadı.");

        public void Acknowledge(long sequence) => _inner.Acknowledge(sequence);

        public int DiscardUncommitted(string volumeId, ulong journalId, long committedUsn) =>
            _inner.DiscardUncommitted(volumeId, journalId, committedUsn);
    }

    private ChangeFeedStoreLayout Layout() =>
        ChangeFeedStoreLayout.ForOwner(_storeRoot.Path, OwnerSid);

    [Fact]
    public void Run_ReanchorsTheCursorWhenTheJournalMovedPastIt()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);

        var reader = CreateReader();
        CreateRunner(store, reader).Run();

        Assert.Equal(BootstrapUsn, ReadState()!.NextUsn);

        reader.Descriptor = new UsnJournalDescriptor(
            JournalId, 5000, 6000, 0, long.MaxValue, 0, 0);

        var wrapped = CreateRunner(store, reader).Run();

        Assert.Equal(1, wrapped.RootsGapped);
        Assert.Contains("CursorOutsideJournal", wrapped.Diagnostics);
        Assert.Equal(
            6000,
            ReadState()!.NextUsn);

        var afterwards = CreateRunner(store, reader).Run();

        Assert.Equal(0, afterwards.RootsGapped);
        Assert.Null(afterwards.Diagnostics);
    }

    [Fact]
    public void Run_KeepsTheEarliestCursorWhenOnlySomeRootsWereReanchored()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path, _secondRoot.Path);

        var reader = CreateReader();
        CreateRunner(store, reader).Run();

        Assert.Equal(BootstrapUsn, ReadState()!.NextUsn);

        reader.Descriptor = new UsnJournalDescriptor(
            JournalId, 5000, 6000, 0, long.MaxValue, 0, 0);
        CreateRunner(store, reader).Run();

        Assert.Equal(6000, ReadState()!.NextUsn);
        Assert.Equal(2, ReadState()!.Roots.Count);
    }

    [Fact]
    public void AHeldLease_KeepsTheServiceInsideTheJournalWindow()
    {
        var store = CreateStore();
        Subscribe(store, _firstRoot.Path);
        var reader = CreateReader();
        CreateRunner(store, reader).Run();
        store.Acknowledge(long.MaxValue);

        store.HoldLease(TimeSpan.FromMinutes(5));

        for (var step = 0; step < 4; step++)
        {
            var first = BootstrapUsn + (step * 500);
            reader.Descriptor = new UsnJournalDescriptor(
                JournalId, first, first + 500, 0, long.MaxValue, 0, 0);
            reader.EnqueuePage(first + 500, new UsnRecordBuffer().Build());
            CreateRunner(store, reader).Run();
        }

        Assert.Empty(store.ReadPending().Entries);

        store.ReleaseLease();
        reader.EnqueuePage(
            reader.Descriptor.NextUsn,
            new UsnRecordBuffer().Build());
        var result = CreateRunner(store, reader).Run();

        Assert.Equal(
            0,
            result.RootsGapped);
        Assert.Equal(UsnDrainOutcome.Completed, result.Outcome);
    }

    private FileSystemChangeFeedStore CreateStore() => new(Layout());

    private UsnDrainRunner CreateRunner(
        IChangeFeedStore store,
        FakeUsnJournalReader reader,
        Func<DateTime>? utcNow = null) =>
        new(
            Layout(),
            store,
            new SingleReaderFactory(reader),
            _probe,
            new FakeUsnSubtreeReader(),
            utcNow);

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
        return new ChangeFeedSubscribedRoot(
            path,
            identity.ToChangeFeedRootIdentity(),
            ChangeFeedRootGeneration.New());
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

    private sealed class CountingReaderFactory : IUsnJournalReaderFactory
    {
        private readonly IUsnJournalReader _reader;

        public CountingReaderFactory(IUsnJournalReader reader)
        {
            _reader = reader;
        }

        public int OpenCount { get; private set; }

        public IUsnJournalReader Open(string volumeRootPath)
        {
            OpenCount++;
            return _reader;
        }
    }

    private sealed class ThrowingReaderFactory : IUsnJournalReaderFactory
    {
        public IUsnJournalReader Open(string volumeRootPath) =>
            throw new UnauthorizedAccessException("Test: birim açılamadı.");
    }
}
