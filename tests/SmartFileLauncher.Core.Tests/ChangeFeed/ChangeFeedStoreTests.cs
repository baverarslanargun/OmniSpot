using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedStoreTests
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private const string FirstRoot = @"C:\Kok";
    private const string SecondRoot = @"C:\Diger";
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";
    private const string OtherVolumeId = "ntfs-vsn:0x000000000000BEEF";

    private static readonly ChangeFeedRootGeneration Generation =
        ChangeFeedRootGeneration.New();

    [Fact]
    public void Subscription_RoundTripsThroughTheStore()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        Assert.Null(store.ReadSubscription());

        store.WriteSubscription(CreateSubscription());
        var restored = store.ReadSubscription();

        Assert.NotNull(restored);
        Assert.Equal(OwnerSid, restored!.OwnerSid);
        Assert.Equal(
            new[] { FirstRoot, SecondRoot },
            restored.Roots.Select(root => root.RootPath));
        Assert.Equal("vol-1", restored.Roots[0].Identity.VolumeId);
        Assert.Equal("node-1", restored.Roots[0].Identity.NodeId);
    }

    [Fact]
    public void ReadSubscription_RejectsARecordItCannotParse()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout);
        File.WriteAllText(layout.SubscriptionPath, "{ bozuk");

        Assert.Throws<InvalidDataException>(() => store.ReadSubscription());
    }

    [Fact]
    public void Subscription_AuthorizesOnlyPathsInsideItsRoots()
    {
        var subscription = CreateSubscription();

        Assert.True(subscription.Authorizes(FirstRoot));
        Assert.True(subscription.Authorizes(@"C:\Kok\alt\rapor.txt"));
        Assert.False(subscription.Authorizes(@"C:\Kok2\rapor.txt"));
        Assert.False(subscription.Authorizes(@"C:\Baska\rapor.txt"));
    }

    [Fact]
    public void Subscription_RefusesATraversalPathThatLeavesTheRoot()
    {
        var subscription = CreateSubscription();

        Assert.False(subscription.Authorizes(@"C:\Kok\..\Baska\rapor.txt"));
        Assert.False(subscription.Authorizes(@"C:\Kok\alt\..\..\Baska\rapor.txt"));
        Assert.False(subscription.Authorizes(@"gorece\yol.txt"));
        Assert.True(subscription.Authorizes(@"C:\Kok\alt\..\rapor.txt"));
    }

    [Fact]
    public void Enqueue_WritesTheOverflowGapWithNoJournalPositionSoRecoveryKeepsIt()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, maximumEntryCount: 1);
        store.WriteSubscription(CreateSubscription());

        store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        var overflow = store.EnqueueOne(VolumeId, JournalId, 200, 300, Deliveries(SecondRoot));

        Assert.False(overflow.IsPositional);
        Assert.Equal(0, store.DiscardUncommitted(VolumeId, JournalId, 100));
        Assert.Equal(overflow.Sequence, Assert.Single(store.ReadPending().Entries).Sequence);
    }

    [Fact]
    public void DiscardUncommitted_LeavesAnotherVolumeAloneWhenTheJournalIdCollides()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        var other = store.EnqueueOne(OtherVolumeId, JournalId, 100, 900, Deliveries(SecondRoot));
        var mine = store.EnqueueOne(VolumeId, JournalId, 100, 900, Deliveries(FirstRoot));

        Assert.Equal(1, store.DiscardUncommitted(VolumeId, JournalId, 100));

        var survivor = Assert.Single(store.ReadPending().Entries);
        Assert.Equal(other.Sequence, survivor.Sequence);
        Assert.Equal(OtherVolumeId, survivor.VolumeId);
        Assert.NotEqual(mine.Sequence, survivor.Sequence);
    }

    [Fact]
    public void Enqueue_KeepsTheBacklogWhenTheOverflowEntryCannotBeWritten()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout, maximumEntryCount: 1);
        store.WriteSubscription(CreateSubscription());

        var backlog = store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        Directory.CreateDirectory(
            Path.Combine(layout.QueueDirectory, "0000000000000000002.json"));

        Assert.NotNull(Record.Exception(
            () => store.EnqueueOne(VolumeId, JournalId, 200, 300, Deliveries(SecondRoot))));

        Assert.Equal(backlog.Sequence, Assert.Single(store.ReadPending().Entries).Sequence);
    }

    [Fact]
    public void Subscription_RejectsMoreRootsThanTheCeilingCanRepresent()
    {
        var roots = Enumerable
            .Range(0, ChangeFeedSubscription.MaximumRoots + 1)
            .Select(index => Root(@"C:\Kok" + index, "vol-1", "node-" + index))
            .ToArray();

        Assert.Throws<ArgumentException>(() => new ChangeFeedSubscription(OwnerSid, roots));
    }

    [Fact]
    public void Subscription_RejectsDuplicateRoots()
    {
        Assert.Throws<ArgumentException>(
            () => new ChangeFeedSubscription(
                OwnerSid,
                new[] { Root(FirstRoot, "vol-1", "node-1"), Root(FirstRoot, "vol-1", "node-1") }));
    }

    [Fact]
    public void Subscription_RejectsARelativeRoot()
    {
        Assert.Throws<ArgumentException>(
            () => Root(@"kok\alt", "vol-1", "node-1"));
    }

    [Fact]
    public void Enqueue_AssignsIncreasingSequencesAndReadsThemInOrder()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        var first = store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        var second = store.EnqueueOne(VolumeId, JournalId, 200, 300, Deliveries(FirstRoot));

        Assert.True(second.Sequence > first.Sequence);
        Assert.Equal(
            new[] { first.Sequence, second.Sequence },
            store.ReadPending().Entries.Select(entry => entry.Sequence));
    }

    [Fact]
    public void Enqueue_RoundTripsEventsGapsAndFaults()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        store.EnqueueOne(
            VolumeId,
            JournalId,
            100,
            200,
            new[]
            {
                new ChangeFeedRootDelivery(
                    FirstRoot,
                    ChangeFeedBatch.Ok(new[]
                    {
                        new ChangeFeedEvent(ChangeFeedEventKind.Created, @"C:\Kok\yeni.txt", false),
                        new ChangeFeedEvent(
                            ChangeFeedEventKind.Renamed,
                            @"C:\Kok\yeni ad",
                            true,
                            @"C:\Kok\eski ad")
                    }),
                    Generation),
                new ChangeFeedRootDelivery(
                    SecondRoot,
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.RootIdentityChanged),
                    Generation)
            });

        var entry = Assert.Single(store.ReadPending().Entries);

        Assert.Equal(JournalId, entry.JournalId);
        Assert.Equal(100, entry.FromUsn);
        Assert.Equal(200, entry.ToUsn);
        Assert.True(entry.HasAnyGap);
        Assert.Equal(2, entry.EventCount);

        var events = entry.Roots[0].Batch.Events;
        Assert.Equal(ChangeFeedEventKind.Created, events[0].Kind);
        Assert.Equal(@"C:\Kok\yeni.txt", events[0].FullPath);
        Assert.Equal(@"C:\Kok\eski ad", events[1].OldPath);
        Assert.True(events[1].IsDirectory);

        Assert.Equal(ChangeFeedStatus.Gap, entry.Roots[1].Batch.Status);
        Assert.Equal(ChangeFeedGapReason.RootIdentityChanged, entry.Roots[1].Batch.GapReason);
    }

    [Fact]
    public void Enqueue_RoundTripsAFaultedDelivery()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        store.EnqueueOne(
            VolumeId,
            JournalId,
            100,
            200,
            new[]
            {
                new ChangeFeedRootDelivery(
                    FirstRoot,
                    ChangeFeedBatch.Faulted(
                        ChangeFeedFaultReason.NativeProtocolRejected,
                        "FSCTL reddedildi"),
                    Generation)
            });

        var batch = Assert.Single(store.ReadPending().Entries).Roots[0].Batch;

        Assert.Equal(ChangeFeedStatus.Faulted, batch.Status);
        Assert.Equal(ChangeFeedFaultReason.NativeProtocolRejected, batch.FaultReason);
        Assert.Equal("FSCTL reddedildi", batch.Diagnostics);
    }

    [Fact]
    public void Acknowledge_DeletesOnlyEntriesUpToTheGivenSequence()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        var first = store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        var second = store.EnqueueOne(VolumeId, JournalId, 200, 300, Deliveries(FirstRoot));

        store.Acknowledge(first.Sequence);

        Assert.Equal(second.Sequence, Assert.Single(store.ReadPending().Entries).Sequence);
    }

    [Fact]
    public void Enqueue_NeverReusesASequenceAfterTheQueueEmpties()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        var first = store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        store.Acknowledge(first.Sequence);
        Assert.Empty(store.ReadPending().Entries);

        var second = store.EnqueueOne(VolumeId, JournalId, 200, 300, Deliveries(FirstRoot));

        Assert.True(second.Sequence > first.Sequence);
    }

    [Fact]
    public void Enqueue_ReplacesTheBacklogWithAnExplicitGapWhenTheEntryLimitIsReached()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, maximumEntryCount: 2);
        store.WriteSubscription(CreateSubscription());

        store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        store.EnqueueOne(VolumeId, JournalId, 200, 300, Deliveries(SecondRoot));
        var overflow = store.EnqueueOne(VolumeId, JournalId, 300, 400, Deliveries(FirstRoot));

        Assert.Equal(overflow.Sequence, Assert.Single(store.ReadPending().Entries).Sequence);
        Assert.False(overflow.IsPositional);
        Assert.Equal(
            new[] { FirstRoot, SecondRoot }.Order(),
            overflow.Roots.Select(root => root.RootPath).Order());
        Assert.All(
            overflow.Roots,
            root => Assert.Equal(
                ChangeFeedGapReason.DeliveryQueueOverflow,
                root.Batch.GapReason));
    }

    [Fact]
    public void Enqueue_ReplacesTheBacklogWhenTheByteLimitIsReached()
    {
        using var directory = new TemporaryDirectory();
        var seed = CreateStore(directory);
        seed.WriteSubscription(CreateSubscription());
        seed.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));

        var overflow = CreateStore(directory, maximumTotalBytes: 16)
            .EnqueueOne(VolumeId, JournalId, 200, 300, Deliveries(SecondRoot));

        Assert.All(
            overflow.Roots,
            root => Assert.Equal(
                ChangeFeedGapReason.DeliveryQueueOverflow,
                root.Batch.GapReason));
        Assert.Equal(overflow.Sequence, Assert.Single(CreateStore(directory).ReadPending().Entries).Sequence);
    }

    [Fact]
    public void AnOverflowWithoutASubscription_WritesNoRecoveryAtAll()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout, maximumEntryCount: 1);

        var history = Enumerable
            .Range(0, ChangeFeedSubscription.MaximumRoots + 44)
            .SelectMany(index => Stale(@"C:\Gecmis" + index))
            .ToArray();

        Assert.Single(store.Enqueue(VolumeId, JournalId, 0, 10, history));

        var overflow = store.Enqueue(VolumeId, JournalId, 10, 20, Deliveries(FirstRoot));

        Assert.Empty(overflow);
        Assert.Empty(store.ReadPending().Entries);
    }

    [Fact]
    public void AnOverflowRecovery_CoversOnlyTheRootsTheSubscriptionStillCarries()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout, maximumEntryCount: 3);

        store.WriteSubscription(new ChangeFeedSubscription(
            OwnerSid,
            new[] { Root(FirstRoot, "vol-1", "node-1") }));

        store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        store.EnqueueOne(VolumeId, JournalId, 200, 300, Stale(SecondRoot));
        store.EnqueueOne(VolumeId, JournalId, 300, 400, Stale(FirstRoot));

        var overflow = store.EnqueueOne(VolumeId, JournalId, 400, 500, Deliveries(FirstRoot));

        Assert.Equal(new[] { FirstRoot }, overflow.Roots.Select(root => root.RootPath));
        Assert.Equal(Generation.Value, overflow.Roots[0].Generation.Value);
    }

    [Fact]
    public void AnOverflowRecovery_StaysWithinTheSubscriptionRootCount()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout, maximumEntryCount: 40);

        store.WriteSubscription(new ChangeFeedSubscription(
            OwnerSid,
            new[]
            {
                Root(FirstRoot, "vol-1", "node-1"),
                Root(SecondRoot, "vol-1", "node-2")
            }));

        foreach (var index in Enumerable.Range(0, 40))
        {
            store.EnqueueOne(
                VolumeId,
                JournalId,
                index,
                index + 1,
                Stale(@"C:\Eski" + index));
        }

        var overflow = store.EnqueueOne(VolumeId, JournalId, 900, 901, Deliveries(FirstRoot));

        Assert.True(
            overflow.Roots.Count <= store.ReadSubscription()!.Roots.Count,
            "Kurtarma abonelik kök sayısını aştı.");
        Assert.Equal(new[] { FirstRoot }, overflow.Roots.Select(root => root.RootPath));
    }

    [Fact]
    public void AMultiPartRepair_StillRespectsTheReadBudget()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(
            layout,
            maximumEntryCount: FileSystemChangeFeedStore.DefaultMaximumEntryCount,
            maximumTotalBytes: FileSystemChangeFeedStore.DefaultMaximumTotalBytes,
            maximumEntryBytes: 2048);

        var roots = Enumerable
            .Range(0, 80)
            .Select(index => Root(@"C:\Kok\" + new string('k', 60) + index, "vol-1", "node-" + index))
            .ToArray();

        store.WriteSubscription(new ChangeFeedSubscription(OwnerSid, roots));
        File.WriteAllText(
            Path.Combine(layout.QueueDirectory, "0000000000000000009.json"),
            "{ bozuk");

        var slice = store.ReadPending(new ChangeFeedReadBudget(2, long.MaxValue));

        Assert.Equal(2, slice.Entries.Count);
        Assert.True(slice.HasMore, "Onarım çok parçalıysa devamı bildirilmeli.");
        Assert.True(
            Directory.GetFiles(layout.QueueDirectory, "*.json").Length > 2,
            "Kurulum çok parçalı bir onarım üretmeliydi.");
    }

    [Fact]
    public void ReadPending_ReplacesACorruptQueueWithAnExplicitGap()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout);
        store.WriteSubscription(CreateSubscription());
        store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        File.WriteAllText(
            Path.Combine(layout.QueueDirectory, "0000000000000000009.json"),
            "{ bozuk");

        var entry = Assert.Single(store.ReadPending().Entries);

        Assert.Equal(
            new[] { FirstRoot, SecondRoot },
            entry.Roots.Select(root => root.RootPath));
        Assert.All(
            entry.Roots,
            root => Assert.Equal(ChangeFeedGapReason.FeedStateInvalid, root.Batch.GapReason));
        Assert.Single(Directory.GetFiles(layout.QueueDirectory));
    }

    [Fact]
    public void ReadPending_ThrowsWhenTheQueueIsCorruptAndNoSubscriptionExists()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout);
        File.WriteAllText(
            Path.Combine(layout.QueueDirectory, "0000000000000000001.json"),
            "{ bozuk");

        Assert.Throws<InvalidDataException>(() => store.ReadPending().Entries);
    }

    [Fact]
    public void DiscardUncommitted_DropsEntriesBeyondTheCommittedCursor()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);

        var committed = store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        store.EnqueueOne(VolumeId, JournalId, 200, 300, Deliveries(FirstRoot));

        Assert.Equal(1, store.DiscardUncommitted(VolumeId, JournalId, 200));
        Assert.Equal(committed.Sequence, Assert.Single(store.ReadPending().Entries).Sequence);
    }

    [Fact]
    public void DiscardUncommitted_KeepsEntriesRecordedAgainstAnotherJournal()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var entry = store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));

        Assert.Equal(0, store.DiscardUncommitted(VolumeId, 99, 500));
        Assert.Equal(entry.Sequence, Assert.Single(store.ReadPending().Entries).Sequence);
    }

    [Fact]
    public void DiscardUncommitted_LeavesUnreadableEntriesForTheRepairPath()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout);
        store.WriteSubscription(CreateSubscription());
        File.WriteAllText(
            Path.Combine(layout.QueueDirectory, "0000000000000000001.json"),
            "{ bozuk");

        Assert.Equal(0, store.DiscardUncommitted(VolumeId, JournalId, 500));

        var repaired = Assert.Single(store.ReadPending().Entries);
        Assert.All(
            repaired.Roots,
            root => Assert.Equal(ChangeFeedGapReason.FeedStateInvalid, root.Batch.GapReason));
    }

    [Fact]
    public void DiscardUncommitted_KeepsGapEntriesThatCarryNoJournalPosition()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, maximumEntryCount: 1);
        store.WriteSubscription(CreateSubscription());
        store.EnqueueOne(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        store.EnqueueOne(VolumeId, JournalId, 0, 0, Deliveries(FirstRoot));

        var survivor = Assert.Single(store.ReadPending().Entries);
        Assert.False(survivor.IsPositional);
        Assert.All(
            survivor.Roots,
            root => Assert.Equal(
                ChangeFeedGapReason.DeliveryQueueOverflow,
                root.Batch.GapReason));

        Assert.Equal(0, store.DiscardUncommitted(VolumeId, 99, 0));
        Assert.Equal(survivor.Sequence, Assert.Single(store.ReadPending().Entries).Sequence);
    }

    [Fact]
    public void EnumerateOwners_ListsEveryOwnerDirectoryAndNothingElse()
    {
        using var directory = new TemporaryDirectory();

        Assert.Empty(ChangeFeedStoreLayout.EnumerateOwners(
            Path.Combine(directory.Path, "yok")));

        CreateStore(directory);
        Directory.CreateDirectory(
            ChangeFeedStoreLayout.ForOwner(directory.Path, "S-1-5-21-1-2-3-1002").OwnerDirectory);
        File.WriteAllText(Path.Combine(directory.Path, "gurultu.txt"), "x");

        Assert.Equal(
            new[] { OwnerSid, "S-1-5-21-1-2-3-1002" }.Order(),
            ChangeFeedStoreLayout.EnumerateOwners(directory.Path).Order());
    }

    [Fact]
    public void ForOwner_RejectsAnOwnerThatCannotBeADirectoryName()
    {
        Assert.Throws<ArgumentException>(
            () => ChangeFeedStoreLayout.ForOwner(@"C:\Depo", @"kotu\ad"));
    }

    private static FileSystemChangeFeedStore CreateStore(
        TemporaryDirectory directory,
        int maximumEntryCount = FileSystemChangeFeedStore.DefaultMaximumEntryCount,
        long maximumTotalBytes = FileSystemChangeFeedStore.DefaultMaximumTotalBytes) =>
        new(
            ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid),
            maximumEntryCount,
            maximumTotalBytes);

    private static ChangeFeedSubscription CreateSubscription() =>
        new(
            OwnerSid,
            new[]
            {
                Root(FirstRoot, "vol-1", "node-1"),
                Root(SecondRoot, "vol-1", "node-2")
            });

    private static ChangeFeedSubscribedRoot Root(string path, string volumeId, string nodeId) =>
        new(
            path,
            new ChangeFeedRootIdentity(volumeId, nodeId),
            Generation);

    private static ChangeFeedRootDelivery[] Stale(string rootPath) =>
        new[]
        {
            new ChangeFeedRootDelivery(
                rootPath,
                ChangeFeedBatch.Gap(ChangeFeedGapReason.FeedStateInvalid),
                ChangeFeedRootGeneration.New())
        };

    private static ChangeFeedRootDelivery[] Deliveries(string rootPath) =>
        new[]
        {
            new ChangeFeedRootDelivery(
                rootPath,
                ChangeFeedBatch.Ok(new[]
                {
                    new ChangeFeedEvent(
                        ChangeFeedEventKind.Created,
                        Path.Combine(rootPath, "yeni.txt"),
                        false)
                }),
                Generation)
        };
}
