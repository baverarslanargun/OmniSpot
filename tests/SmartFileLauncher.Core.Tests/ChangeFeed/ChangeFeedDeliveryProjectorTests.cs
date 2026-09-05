using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedDeliveryProjectorTests
{
    private const string OwnerSid = "S-1-5-21-1-2-3-1001";
    private const string Root = @"C:\Kok";
    private const string SecondRoot = @"C:\KokIki";
    private const string Closed = @"C:\Kok\Kapali";
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";

    private static readonly ChangeFeedRootGeneration Generation = ChangeFeedRootGeneration.New();

    [Fact]
    public void AuthorizedEvents_ArePublishedAndTheEntryCountsAsComplete()
    {
        var page = Project(Entry(1, Delivery(Created(@"C:\Kok\Acik\a.txt"))));

        var root = Assert.Single(page.Roots);
        Assert.Equal(@"C:\Kok\Acik\a.txt", Assert.Single(root.Events).FullPath);
        Assert.False(root.HasAnyGap);
        Assert.Equal(1, page.CompletedThroughSequence);
        Assert.False(page.HasMore);
    }

    [Fact]
    public void WithheldNames_BecomeALocationlessAuthorizationGap()
    {
        var page = Project(Entry(1, Delivery(Created(@"C:\Kok\Kapali\gizli.txt"))));

        var root = Assert.Single(page.Roots);
        Assert.Empty(root.Events);
        Assert.True(root.AuthorizationGap);
        Assert.Equal(ChangeFeedGapReason.None, root.ProducerGap);
        Assert.Equal(Root, root.RootPath);
    }

    [Fact]
    public void ProducerGaps_StayDistinguishableFromAuthorizationGaps()
    {
        var entry = Entry(
            1,
            new ChangeFeedRootDelivery(
                Root,
                ChangeFeedBatch.Gap(ChangeFeedGapReason.DeliveryQueueOverflow),
                Generation));

        var root = Assert.Single(Project(entry).Roots);

        Assert.Equal(ChangeFeedGapReason.DeliveryQueueOverflow, root.ProducerGap);
        Assert.Equal(ChangeFeedFaultReason.None, root.ProducerFault);
        Assert.False(root.AuthorizationGap);
        Assert.False(root.PayloadTooLarge);
    }

    [Fact]
    public void AFaultedDelivery_IsReportedInsteadOfSilentlyCompleted()
    {
        var page = Project(Entry(4, Faulted()));

        var root = Assert.Single(page.Roots);
        Assert.Equal(ChangeFeedFaultReason.NativeProtocolRejected, root.ProducerFault);
        Assert.Equal(ChangeFeedGapReason.None, root.ProducerGap);
        Assert.False(root.AuthorizationGap);
        Assert.True(root.HasAnyGap);
        Assert.Equal(4, page.CompletedThroughSequence);
    }

    [Fact]
    public void AFaultedDelivery_DoesNotCarryItsDiagnostics()
    {
        var page = Project(Entry(4, Faulted()));

        Assert.DoesNotContain("yerel-protokol", Render(page), StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ASingleOversizedEvent_BecomesAPayloadGapSeparateFromAuthorization()
    {
        var page = Project(
            budget: 40,
            Entry(1, Delivery(Created(@"C:\Kok\Acik\cok-uzun-bir-belge-adi.txt"))));

        var root = Assert.Single(page.Roots);
        Assert.Empty(root.Events);
        Assert.True(root.PayloadTooLarge);
        Assert.False(root.AuthorizationGap);
    }

    [Fact]
    public void AnUnauthorizedOversizedEvent_LooksExactlyLikeAnUnauthorizedSmallOne()
    {
        var small = Project(
            budget: 40,
            Entry(1, Delivery(Created(@"C:\Kok\Kapali\a.txt"))));

        var large = Project(
            budget: 40,
            Entry(1, Delivery(Created(@"C:\Kok\Kapali\cok-cok-uzun-gizli-belge-adi.txt"))));

        Assert.Equal(Render(small), Render(large));
    }

    [Fact]
    public void ManyHiddenEventsOnOneRoot_CollapseToASingleAuthorizationGap()
    {
        var page = Project(
            Entry(1, Delivery(Created(@"C:\Kok\Kapali\a.txt"))),
            Entry(2, Delivery(Created(@"C:\Kok\Kapali\b.txt"))),
            Entry(3, Delivery(Created(@"C:\Kok\Kapali\c.txt"))));

        var root = Assert.Single(page.Roots);
        Assert.True(root.AuthorizationGap);
        Assert.Empty(root.Events);
        Assert.Equal(3, page.CompletedThroughSequence);
    }

    [Fact]
    public void OneHiddenEvent_LooksExactlyLikeManyHiddenEvents()
    {
        var one = Project(Entry(1, Delivery(Created(@"C:\Kok\Kapali\a.txt"))));

        var many = Project(
            Entry(1, Delivery(
                Created(@"C:\Kok\Kapali\a.txt"),
                Created(@"C:\Kok\Kapali\b.txt"))),
            Entry(2, Delivery(Created(@"C:\Kok\Kapali\c.txt"))));

        Assert.Equal(RootShapeOf(one), RootShapeOf(many));
    }

    [Fact]
    public void ManyProducerGapsOnOneRoot_CollapseToASingleGap()
    {
        var entries = Enumerable.Range(1, 64)
            .Select(sequence => Entry(
                sequence,
                new ChangeFeedRootDelivery(
                    Root,
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.DeliveryQueueOverflow),
                    Generation)))
            .ToArray();

        var page = Project(entries);

        var root = Assert.Single(page.Roots);
        Assert.Equal(ChangeFeedGapReason.DeliveryQueueOverflow, root.ProducerGap);
    }

    [Fact]
    public void RootMetadata_IsChargedToThePageBudget()
    {
        var page = Project(
            budget: 40,
            Entry(1, Delivery(Created(@"C:\Kok\Acik\a.txt")), SecondDelivery()));

        var root = Assert.Single(page.Roots);
        Assert.Equal(Root, root.RootPath);
        Assert.Equal(0, page.CompletedThroughSequence);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void APartiallyProjectedEntry_DoesNotCountAsCompleted()
    {
        var entry = Entry(
            5,
            Delivery(
                Created(@"C:\Kok\Acik\a.txt"),
                Created(@"C:\Kok\Acik\b.txt"),
                Created(@"C:\Kok\Acik\c.txt")));

        var page = Project(budget: 40, entry);

        Assert.Equal(0, page.CompletedThroughSequence);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void AnEarlierCompleteEntry_StaysTheCompletedPrefixWhenALaterOneIsCut()
    {
        var page = Project(
            budget: 40,
            Entry(3, Delivery(Created(@"C:\Kok\Acik\a.txt"))),
            Entry(4, Delivery(
                Created(@"C:\Kok\Acik\b.txt"),
                Created(@"C:\Kok\Acik\c.txt"))));

        Assert.Equal(3, page.CompletedThroughSequence);
        Assert.True(page.HasMore);
    }

    [Fact]
    public void AStaleGeneration_IsNotDelivered()
    {
        var entry = Entry(
            1,
            new ChangeFeedRootDelivery(
                Root,
                ChangeFeedBatch.Ok(new[] { Created(@"C:\Kok\Acik\a.txt") }),
                ChangeFeedRootGeneration.New()));

        var page = Project(entry);

        Assert.Empty(page.Roots);
        Assert.Equal(1, page.CompletedThroughSequence);
    }

    [Fact]
    public void AnUnsubscribedOwner_ReceivesNothing()
    {
        var projector = new ChangeFeedDeliveryProjector(Authorizer, new Measure(), 1024);

        var page = projector.Project(null, Slice(Entry(1, Delivery(Created(@"C:\Kok\Acik\a.txt")))));

        Assert.Empty(page.Roots);
    }

    [Fact]
    public void MoreEntriesInTheStore_AreReportedAsMore()
    {
        var projector = new ChangeFeedDeliveryProjector(Authorizer, new Measure(), 1024);
        var slice = new ChangeFeedQueueSlice(
            new[] { Entry(1, Delivery(Created(@"C:\Kok\Acik\a.txt"))) },
            hasMore: true);

        Assert.True(projector.Project(Subscription(), slice).HasMore);
    }

    [Fact]
    public void APageBudgetThatCannotHoldTheEnvelope_IsRefused()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new ChangeFeedDeliveryProjector(Authorizer, new Measure(), 1));
    }

    private static string Render(ChangeFeedDeliveryPage page) =>
        string.Join(
            ";",
            page.Roots.Select(root =>
                $"{root.RootPath}|{root.ProducerGap}|{root.ProducerFault}|" +
                $"{root.AuthorizationGap}|{root.PayloadTooLarge}|" +
                string.Join(
                    ",",
                    root.Events.Select(change =>
                        $"{change.Kind}:{change.FullPath}:{change.OldPath}:{change.IsDirectory}")))) +
        $"#{page.CompletedThroughSequence}#{page.HasMore}";

    private static string RootShapeOf(ChangeFeedDeliveryPage page) =>
        string.Join(
            ";",
            page.Roots.Select(root =>
                $"{root.RootPath}|{root.ProducerGap}|{root.ProducerFault}|" +
                $"{root.AuthorizationGap}|{root.PayloadTooLarge}|{root.Events.Count}"));

    private static ChangeFeedDeliveryPage Project(params ChangeFeedQueueEntry[] entries) =>
        Project(1024, entries);

    private static ChangeFeedDeliveryPage Project(long budget, params ChangeFeedQueueEntry[] entries) =>
        new ChangeFeedDeliveryProjector(Authorizer, new Measure(), budget)
            .Project(Subscription(), Slice(entries));

    private static ChangeFeedQueueSlice Slice(params ChangeFeedQueueEntry[] entries) =>
        new(entries, hasMore: false);

    private static ChangeFeedPathAuthorizer Authorizer(string rootPath) =>
        new(rootPath, directory =>
            !directory.StartsWith(Closed, StringComparison.OrdinalIgnoreCase));

    private sealed class Measure : IChangeFeedPageMeasure
    {
        public long Envelope => 2;

        public long Root(string rootPath) => rootPath.Length;

        public long Event(ChangeFeedEvent change) =>
            change.FullPath.Length + (change.OldPath?.Length ?? 0);
    }

    private static ChangeFeedQueueEntry Entry(long sequence, params ChangeFeedRootDelivery[] roots) =>
        new(sequence, VolumeId, JournalId, 0, 10, roots);

    private static ChangeFeedRootDelivery Delivery(params ChangeFeedEvent[] events) =>
        new(Root, ChangeFeedBatch.Ok(events), Generation);

    private static ChangeFeedRootDelivery SecondDelivery() =>
        new(
            SecondRoot,
            ChangeFeedBatch.Ok(new[] { Created(@"C:\KokIki\b.txt") }),
            Generation);

    private static ChangeFeedRootDelivery Faulted() =>
        new(
            Root,
            ChangeFeedBatch.Faulted(
                ChangeFeedFaultReason.NativeProtocolRejected,
                "yerel-protokol reddi"),
            Generation);

    private static ChangeFeedEvent Created(string path) =>
        new(ChangeFeedEventKind.Created, path, false);

    private static ChangeFeedSubscription Subscription() =>
        new(
            OwnerSid,
            new[]
            {
                new ChangeFeedSubscribedRoot(
                    Root,
                    new ChangeFeedRootIdentity("vol", "node"),
                    Generation),
                new ChangeFeedSubscribedRoot(
                    SecondRoot,
                    new ChangeFeedRootIdentity("vol", "node-iki"),
                    Generation)
            });
}
