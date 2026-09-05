using System.Runtime.Versioning;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedRootGenerationTests : IDisposable
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";

    private readonly TemporaryDirectory _storeRoot = new();

    public void Dispose() => _storeRoot.Dispose();

    [Fact]
    public void AGeneration_IsNeverUnknownAndNeverRepeats()
    {
        var first = ChangeFeedRootGeneration.New();
        var second = ChangeFeedRootGeneration.New();

        Assert.False(first.IsUnknown);
        Assert.False(first.Matches(second));
        Assert.True(first.Matches(first));
    }

    [Fact]
    public void AnUnknownGeneration_MatchesNothingIncludingItself()
    {
        var unknown = ChangeFeedRootGeneration.Unknown;

        Assert.True(unknown.IsUnknown);
        Assert.False(unknown.Matches(unknown));
        Assert.False(unknown.Matches(ChangeFeedRootGeneration.New()));
    }

    [Fact]
    public void AGeneration_SurvivesTheStoreRoundTrip()
    {
        var store = CreateStore();
        var root = Root(@"C:\Kok");
        store.WriteSubscription(new ChangeFeedSubscription(OwnerSid, new[] { root }));

        var restored = CreateStore().ReadSubscription();

        Assert.Equal(root.Generation, Assert.Single(restored!.Roots).Generation);
    }

    [Fact]
    public void ADeliveryGeneration_SurvivesTheQueueRoundTrip()
    {
        var store = CreateStore();
        var generation = ChangeFeedRootGeneration.New();

        store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery(@"C:\Kok", generation) });

        var delivered = Assert.Single(Assert.Single(ReadAll(CreateStore())).Roots);
        Assert.Equal(generation, delivered.Generation);
    }

    [Fact]
    public void BacklogFromTheCurrentGeneration_IsDelivered()
    {
        var root = Root(@"C:\Kok");
        var subscription = new ChangeFeedSubscription(OwnerSid, new[] { root });
        var entry = Entry(Delivery(root.RootPath, root.Generation));

        Assert.Single(ChangeFeedGenerationFilter.Current(subscription, entry));
    }

    [Fact]
    public void BacklogFromAnEarlierGeneration_IsNotDelivered()
    {
        var root = Root(@"C:\Kok");
        var subscription = new ChangeFeedSubscription(OwnerSid, new[] { root });
        var entry = Entry(Delivery(root.RootPath, ChangeFeedRootGeneration.New()));

        Assert.Empty(ChangeFeedGenerationFilter.Current(subscription, entry));
    }

    [Fact]
    public void BacklogWithoutAGeneration_IsFailClosed()
    {
        var root = Root(@"C:\Kok");
        var subscription = new ChangeFeedSubscription(OwnerSid, new[] { root });
        var entry = Entry(Delivery(root.RootPath, ChangeFeedRootGeneration.Unknown));

        Assert.Empty(ChangeFeedGenerationFilter.Current(subscription, entry));
    }

    [Fact]
    public void BacklogForARootThatIsNoLongerSubscribed_IsNotDelivered()
    {
        var kept = Root(@"C:\Kok");
        var removed = Root(@"C:\Kaldirilan");
        var subscription = new ChangeFeedSubscription(OwnerSid, new[] { kept });
        var entry = Entry(
            Delivery(kept.RootPath, kept.Generation),
            Delivery(removed.RootPath, removed.Generation));

        var delivered = ChangeFeedGenerationFilter.Current(subscription, entry);

        Assert.Equal(kept.RootPath, Assert.Single(delivered).RootPath);
    }

    [Fact]
    public void BacklogSurvivingASubscriptionDeleteAndRestart_IsNotResurrected()
    {
        var store = CreateStore();
        var original = Root(@"C:\Kok");
        store.WriteSubscription(new ChangeFeedSubscription(OwnerSid, new[] { original }));
        store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery(original.RootPath, original.Generation) });

        store.DeleteSubscription();

        var afterRestart = CreateStore();
        var readded = Root(original.RootPath);
        afterRestart.WriteSubscription(new ChangeFeedSubscription(OwnerSid, new[] { readded }));

        var subscription = afterRestart.ReadSubscription();
        var stale = Assert.Single(ReadAll(afterRestart));

        Assert.NotEqual(original.Generation, readded.Generation);
        Assert.Empty(ChangeFeedGenerationFilter.Current(subscription, stale));
    }

    [Fact]
    public void OverflowAndRepairGaps_KeepTheGenerationOfTheirRoot()
    {
        var store = CreateStore(maximumEntryCount: 1);
        var root = Root(@"C:\Kok");
        store.WriteSubscription(new ChangeFeedSubscription(OwnerSid, new[] { root }));

        store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery(root.RootPath, root.Generation) });
        store.Enqueue(VolumeId, JournalId, 10, 20, new[] { Delivery(root.RootPath, root.Generation) });

        var overflow = Assert.Single(Assert.Single(ReadAll(store)).Roots);

        Assert.Equal(ChangeFeedGapReason.DeliveryQueueOverflow, overflow.Batch.GapReason);
        Assert.Equal(root.Generation, overflow.Generation);
    }

    private static ChangeFeedQueueEntry Entry(params ChangeFeedRootDelivery[] deliveries) =>
        new(1, VolumeId, JournalId, 0, 10, deliveries);

    private static ChangeFeedRootDelivery Delivery(
        string rootPath,
        ChangeFeedRootGeneration generation) =>
        new(
            rootPath,
            ChangeFeedBatch.Ok(new[]
            {
                new ChangeFeedEvent(
                    ChangeFeedEventKind.Created,
                    Path.Combine(rootPath, "yeni.txt"),
                    false)
            }),
            generation);

    private static ChangeFeedSubscribedRoot Root(string path) =>
        new(
            path,
            new ChangeFeedRootIdentity("ntfs-vsn:0x0000000000000001", "0x0000000000000002"),
            ChangeFeedRootGeneration.New());

    private static IReadOnlyList<ChangeFeedQueueEntry> ReadAll(IChangeFeedStore store) =>
        store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue)).Entries;

    private FileSystemChangeFeedStore CreateStore(int maximumEntryCount = 512) =>
        new(
            ChangeFeedStoreLayout.ForOwner(_storeRoot.Path, OwnerSid),
            maximumEntryCount);
}
