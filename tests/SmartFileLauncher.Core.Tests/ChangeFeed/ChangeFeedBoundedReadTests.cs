using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedBoundedReadTests
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private const string Root = @"C:\Kok";
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";

    private static readonly ChangeFeedRootGeneration Generation =
        ChangeFeedRootGeneration.New();

    [Fact]
    public void DefaultBudget_StaysAtTheFrozenNumbers()
    {
        Assert.Equal(64, ChangeFeedReadBudget.DefaultMaximumEntries);
        Assert.Equal(512L * 1024, ChangeFeedReadBudget.DefaultMaximumBytes);
        Assert.Equal(64, ChangeFeedReadBudget.Default.MaximumEntries);
        Assert.Equal(512L * 1024, ChangeFeedReadBudget.Default.MaximumBytes);
    }

    [Fact]
    public void TheProducerCap_NeverExceedsWhatOneReadCanConsume()
    {
        Assert.Equal(
            ChangeFeedReadBudget.DefaultMaximumBytes,
            FileSystemChangeFeedStore.DefaultMaximumEntryBytes);
    }

    [Fact]
    public void OneRead_AlwaysFitsInOneResponse()
    {
        Assert.True(
            2 * ChangeFeedReadBudget.DefaultMaximumBytes <= ChangeFeedProtocol.MaximumResponseBytes,
            "Bir okumanın tamamı tek bir yanıta iki kat payla sığmalıdır; " +
            "aksi hâlde aynı yığın birden çok yanıta bölünür ve yeniden okunur.");
    }

    [Fact]
    public void EntryBudget_NeverOpensTheFileBeyondTheBudget()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var sequences = Fill(store, 5);

        using var locked = Lock(directory, sequences[3]);

        var slice = store.ReadPending(new ChangeFeedReadBudget(3, long.MaxValue));

        Assert.Equal(3, slice.Entries.Count);
        Assert.True(slice.HasMore);
        Assert.Equal(sequences.Take(3), slice.Entries.Select(entry => entry.Sequence));
    }

    [Fact]
    public void ByteBudget_NeverOpensTheFileBeyondTheBudget()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var sequences = Fill(store, 5);

        var firstTwo = EntryBytes(directory, sequences[0]) + EntryBytes(directory, sequences[1]);
        using var locked = Lock(directory, sequences[2]);

        var slice = store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, firstTwo));

        Assert.Equal(2, slice.Entries.Count);
        Assert.True(slice.HasMore);
    }

    [Fact]
    public void EagerReading_WouldFailTheseTests()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var sequences = Fill(store, 5);

        using var locked = Lock(directory, sequences[3]);

        Assert.Throws<IOException>(
            () => store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue)));
    }

    [Fact]
    public void ASingleEntryOverTheByteBudget_IsStillReadSoTheQueueCannotJam()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var sequences = Fill(store, 3);

        using var locked = Lock(directory, sequences[1]);

        var slice = store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, 1));

        Assert.Equal(sequences[0], Assert.Single(slice.Entries).Sequence);
        Assert.True(slice.HasMore);
    }

    [Fact]
    public void ARealEntryOverTheDefaultBudget_IsReadAndTheQueueDrains()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);

        var lenient = new FileSystemChangeFeedStore(
            layout,
            maximumEntryBytes: 8L * 1024 * 1024);

        var oversized = lenient.EnqueueOne(
            VolumeId,
            JournalId,
            0,
            5,
            new[]
            {
                new ChangeFeedRootDelivery(
                    Root,
                    ChangeFeedBatch.Ok(Enumerable
                        .Range(0, 6000)
                        .Select(index => new ChangeFeedEvent(
                            ChangeFeedEventKind.Created,
                            Path.Combine(Root, new string('u', 100) + index + ".txt"),
                            false))
                        .ToArray()),
                    Generation)
            }).Sequence;

        var later = lenient.EnqueueOne(VolumeId, JournalId, 10, 15, Deliveries(1)).Sequence;

        var onDisk = new FileInfo(EntryPath(directory, oversized)).Length;
        Assert.True(
            onDisk > ChangeFeedReadBudget.DefaultMaximumBytes,
            $"Kurulum gerçekten taşan bir girdi üretmeliydi: {onDisk}");

        var store = CreateStore(directory);

        var first = store.ReadPending();
        Assert.Equal(oversized, Assert.Single(first.Entries).Sequence);
        Assert.True(first.HasMore);
        Assert.NotEmpty(first.Entries[0].Roots.SelectMany(root => root.Batch.Events));

        store.Acknowledge(oversized);

        var second = store.ReadPending();
        Assert.Equal(later, Assert.Single(second.Entries).Sequence);
        Assert.False(second.HasMore);

        store.Acknowledge(later);
        Assert.Empty(store.ReadPending().Entries);
    }

    [Fact]
    public void DrainedQueue_ReportsNoMore()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        Fill(store, 2);

        var slice = store.ReadPending(new ChangeFeedReadBudget(64, long.MaxValue));

        Assert.Equal(2, slice.Entries.Count);
        Assert.False(slice.HasMore);
    }

    [Fact]
    public void RepeatedReads_ReachEveryEntryAfterAcknowledgement()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory);
        var sequences = Fill(store, 7);
        var budget = new ChangeFeedReadBudget(2, long.MaxValue);

        var seen = new List<long>();
        for (var round = 0; round < 4; round++)
        {
            var slice = store.ReadPending(budget);
            seen.AddRange(slice.Entries.Select(entry => entry.Sequence));
            store.Acknowledge(slice.Entries[^1].Sequence);
        }

        Assert.Equal(sequences, seen);
        Assert.Empty(store.ReadPending(budget).Entries);
    }

    private static long[] Fill(IChangeFeedStore store, int count) =>
        Enumerable
            .Range(0, count)
            .Select(index => store
                .EnqueueOne(VolumeId, JournalId, index * 10, (index * 10) + 5, Deliveries(index))
                .Sequence)
            .ToArray();

    private static ChangeFeedRootDelivery[] Deliveries(int index) =>
        new[]
        {
            new ChangeFeedRootDelivery(
                Root,
                ChangeFeedBatch.Ok(new[]
                {
                    new ChangeFeedEvent(
                        ChangeFeedEventKind.Created,
                        Path.Combine(Root, $"yeni-{index}.txt"),
                        false)
                }),
                Generation)
        };

    private static FileStream Lock(TemporaryDirectory directory, long sequence) =>
        new(
            EntryPath(directory, sequence),
            FileMode.Open,
            FileAccess.Read,
            FileShare.None);

    private static long EntryBytes(TemporaryDirectory directory, long sequence) =>
        new FileInfo(EntryPath(directory, sequence)).Length;

    private static string EntryPath(TemporaryDirectory directory, long sequence)
    {
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        return Directory
            .GetFiles(layout.QueueDirectory, "*.json")
            .Single(file => long.Parse(Path.GetFileNameWithoutExtension(file)) == sequence);
    }

    private static FileSystemChangeFeedStore CreateStore(TemporaryDirectory directory) =>
        new(ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid));
}
