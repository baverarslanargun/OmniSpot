using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedQueueEpochTests
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private const string FirstRoot = @"C:\Kok";
    private const string SecondRoot = @"C:\Diger";
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";

    private static readonly ChangeFeedRootGeneration Generation =
        ChangeFeedRootGeneration.New();

    [Fact]
    public void TheEpoch_IsTheSameForEveryStoreOverTheSameOwner()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);

        var first = new FileSystemChangeFeedStore(layout).ReadEpoch();
        var second = new FileSystemChangeFeedStore(layout).ReadEpoch();

        Assert.False(first.IsUnknown);
        Assert.True(first.Matches(second));
    }

    [Fact]
    public void AHealthyAppend_LeavesTheEpochAlone()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        store.WriteSubscription(Subscription());

        var before = store.ReadEpoch();
        store.Enqueue(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));

        Assert.True(before.Matches(store.ReadEpoch()));
    }

    [Fact]
    public void AnOverflow_ChangesTheEpoch()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, maximumEntryCount: 1);
        store.WriteSubscription(Subscription());

        store.Enqueue(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));
        var before = store.ReadEpoch();

        store.Enqueue(VolumeId, JournalId, 200, 300, Deliveries(SecondRoot));

        Assert.False(before.Matches(store.ReadEpoch()));
    }

    [Fact]
    public void ARepair_ChangesTheEpoch()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout);
        store.WriteSubscription(Subscription());
        store.Enqueue(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));

        var before = store.ReadEpoch();
        File.WriteAllText(
            Path.Combine(layout.QueueDirectory, "0000000000000000009.json"),
            "{ bozuk");

        store.ReadPending();

        Assert.False(before.Matches(store.ReadEpoch()));
    }

    [Fact]
    public void AnAcknowledge_ChangesTheEpochOnlyWhenItDeletes()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        store.WriteSubscription(Subscription());
        var entry = Assert.Single(store.Enqueue(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot)));

        var before = store.ReadEpoch();
        store.Acknowledge(entry.Sequence - 1);
        Assert.True(before.Matches(store.ReadEpoch()));

        store.Acknowledge(entry.Sequence);
        Assert.False(before.Matches(store.ReadEpoch()));
    }

    [Fact]
    public void ADiscard_ChangesTheEpochOnlyWhenItDeletes()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        store.WriteSubscription(Subscription());
        store.Enqueue(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));

        var before = store.ReadEpoch();
        Assert.Equal(0, store.DiscardUncommitted(VolumeId, JournalId, 200));
        Assert.True(before.Matches(store.ReadEpoch()));

        Assert.Equal(1, store.DiscardUncommitted(VolumeId, JournalId, 100));
        Assert.False(before.Matches(store.ReadEpoch()));
    }

    [Fact]
    public void AFailedEpochWrite_StopsAnAcknowledgeBeforeItDeletes()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout);
        store.WriteSubscription(Subscription());
        var entry = Assert.Single(store.Enqueue(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot)));

        var before = store.ReadEpoch();
        BlockEpochWrites(layout);

        Assert.NotNull(Record.Exception(() => store.Acknowledge(entry.Sequence)));
        Assert.True(before.Matches(store.ReadEpoch()));
        Assert.Single(store.ReadPending().Entries);
    }

    [Fact]
    public void AFailedEpochWrite_StopsADiscardBeforeItDeletes()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout);
        store.WriteSubscription(Subscription());
        store.Enqueue(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot));

        var before = store.ReadEpoch();
        BlockEpochWrites(layout);

        Assert.NotNull(Record.Exception(
            () => store.DiscardUncommitted(VolumeId, JournalId, 100)));
        Assert.True(before.Matches(store.ReadEpoch()));
        Assert.Single(store.ReadPending().Entries);
    }

    [Fact]
    public void AFailedEpochWrite_StopsAnOverflowBeforeItReplacesTheQueue()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(layout, maximumEntryCount: 1);
        store.WriteSubscription(Subscription());
        var kept = Assert.Single(store.Enqueue(VolumeId, JournalId, 100, 200, Deliveries(FirstRoot)));

        var before = store.ReadEpoch();
        BlockEpochWrites(layout);

        Assert.NotNull(Record.Exception(
            () => store.Enqueue(VolumeId, JournalId, 200, 300, Deliveries(SecondRoot))));
        Assert.True(before.Matches(store.ReadEpoch()));
        Assert.Equal(kept.Sequence, Assert.Single(store.ReadPending().Entries).Sequence);
    }

    private static void BlockEpochWrites(ChangeFeedStoreLayout layout) =>
        Directory.CreateDirectory(
            layout.EpochPath + "." + Environment.ProcessId.ToString() + ".tmp");

    private static FileSystemChangeFeedStore CreateStore(
        TemporaryDirectory directory,
        int maximumEntryCount = FileSystemChangeFeedStore.DefaultMaximumEntryCount) =>
        new(
            ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid),
            maximumEntryCount: maximumEntryCount);

    private static ChangeFeedSubscription Subscription() =>
        new(
            OwnerSid,
            new[]
            {
                new ChangeFeedSubscribedRoot(
                    FirstRoot,
                    new ChangeFeedRootIdentity("vol-1", "node-1"),
                    Generation),
                new ChangeFeedSubscribedRoot(
                    SecondRoot,
                    new ChangeFeedRootIdentity("vol-1", "node-2"),
                    Generation)
            });

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
