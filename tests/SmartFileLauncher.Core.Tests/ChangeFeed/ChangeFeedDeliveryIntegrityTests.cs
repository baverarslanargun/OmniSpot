using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedDeliveryIntegrityTests : IDisposable
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private const string Root = @"C:\Kok";
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";

    private readonly TemporaryDirectory _storeRoot = new();

    public void Dispose() => _storeRoot.Dispose();

    [Fact]
    public void ASplitDrain_KeepsEveryPartOnTheCurrentGeneration()
    {
        var subscribed = SubscribedRoot();
        var store = CreateStore(maximumEntryBytes: 16 * 1024);
        store.WriteSubscription(new ChangeFeedSubscription(OwnerSid, new[] { subscribed }));

        var written = store.Enqueue(
            VolumeId,
            JournalId,
            0,
            10,
            new[] { Delivery(subscribed.Generation, 400) });

        Assert.True(written.Count > 1, $"Bölme olmadı: {written.Count}.");

        var subscription = store.ReadSubscription();
        var pending = ReadAll(store);
        var delivered = pending
            .SelectMany(entry => ChangeFeedGenerationFilter.Current(subscription, entry))
            .SelectMany(delivery => delivery.Batch.Events)
            .Count();

        Assert.Equal(400, delivered);
    }

    [Fact]
    public void Overflow_KeepsAGapForTheCurrentGenerationEvenWhenStaleBacklogSharesThePath()
    {
        var stale = ChangeFeedRootGeneration.New();
        var subscribed = SubscribedRoot();
        var store = CreateStore(maximumEntryCount: 1);
        store.WriteSubscription(new ChangeFeedSubscription(OwnerSid, new[] { subscribed }));

        store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery(stale, 1) });
        store.Enqueue(VolumeId, JournalId, 10, 20, new[] { Delivery(subscribed.Generation, 1) });

        var subscription = store.ReadSubscription();
        var surviving = ReadAll(store)
            .SelectMany(entry => ChangeFeedGenerationFilter.Current(subscription, entry))
            .ToArray();

        var gap = Assert.Single(surviving);
        Assert.Equal(ChangeFeedGapReason.DeliveryQueueOverflow, gap.Batch.GapReason);
        Assert.Equal(subscribed.Generation, gap.Generation);
    }

    [Fact]
    public void ByteBudget_NeverOpensAFileThatWouldPushItOverTheLimit()
    {
        var store = CreateStore();
        var generation = ChangeFeedRootGeneration.New();
        var sequences = new[]
        {
            store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery(generation, 1) })[0].Sequence,
            store.Enqueue(VolumeId, JournalId, 10, 20, new[] { Delivery(generation, 40) })[0].Sequence
        };

        var first = EntryBytes(sequences[0]);
        var second = EntryBytes(sequences[1]);
        Assert.True(second > first, "İkinci girdi birinciden büyük olmalı.");

        using var locked = Lock(sequences[1]);

        var slice = store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, first + second - 1));

        Assert.Single(slice.Entries);
        Assert.True(slice.HasMore);
    }

    [Fact]
    public void ByteBudget_StopsBeforeTheSecondEntryWhenTwoWouldExceedIt()
    {
        var store = CreateStore();
        var generation = ChangeFeedRootGeneration.New();
        store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery(generation, 20) });
        store.Enqueue(VolumeId, JournalId, 10, 20, new[] { Delivery(generation, 20) });

        var total = QueueFiles().Sum(file => new FileInfo(file).Length);
        var slice = store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, total - 1));

        Assert.Single(slice.Entries);
        Assert.True(slice.HasMore);
    }

    private static ChangeFeedRootDelivery Delivery(ChangeFeedRootGeneration generation, int events) =>
        new(
            Root,
            ChangeFeedBatch.Ok(
                Enumerable
                    .Range(0, events)
                    .Select(index => new ChangeFeedEvent(
                        ChangeFeedEventKind.Created,
                        Path.Combine(Root, $"belge-{index:D5}.txt"),
                        false))
                    .ToArray()),
            generation);

    private static ChangeFeedSubscribedRoot SubscribedRoot() =>
        new(
            Root,
            new ChangeFeedRootIdentity("ntfs-vsn:0x0000000000000001", "0x0000000000000002"),
            ChangeFeedRootGeneration.New());

    private static IReadOnlyList<ChangeFeedQueueEntry> ReadAll(IChangeFeedStore store) =>
        store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue)).Entries;

    private string[] QueueFiles() =>
        Directory.GetFiles(Layout().QueueDirectory, "*.json");

    private long EntryBytes(long sequence) => new FileInfo(EntryPath(sequence)).Length;

    private FileStream Lock(long sequence) =>
        new(EntryPath(sequence), FileMode.Open, FileAccess.Read, FileShare.None);

    private string EntryPath(long sequence) =>
        QueueFiles().Single(file =>
            long.Parse(Path.GetFileNameWithoutExtension(file)) == sequence);

    private ChangeFeedStoreLayout Layout() =>
        ChangeFeedStoreLayout.ForOwner(_storeRoot.Path, OwnerSid);

    private FileSystemChangeFeedStore CreateStore(
        int maximumEntryCount = 512,
        long maximumEntryBytes = FileSystemChangeFeedStore.DefaultMaximumEntryBytes) =>
        new(
            Layout(),
            maximumEntryCount,
            FileSystemChangeFeedStore.DefaultMaximumTotalBytes,
            maximumEntryBytes);
}
