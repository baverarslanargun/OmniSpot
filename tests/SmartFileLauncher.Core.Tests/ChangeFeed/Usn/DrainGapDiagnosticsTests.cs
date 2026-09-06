using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed.Usn;

public sealed class DrainGapDiagnosticsTests
{
    [Fact]
    public void NoGap_ProducesNoText()
    {
        Assert.Null(UsnDrainRunner.DescribeGaps(new[]
        {
            Delivery(@"C:\kok", ChangeFeedBatch.Ok(Array.Empty<ChangeFeedEvent>())),
        }));
    }

    [Fact]
    public void OneReasonAcrossManyRoots_IsGroupedWithACount()
    {
        var text = UsnDrainRunner.DescribeGaps(new[]
        {
            Gap(@"C:\bir", ChangeFeedGapReason.RootUnavailable),
            Gap(@"C:\iki", ChangeFeedGapReason.RootUnavailable),
            Gap(@"C:\uc", ChangeFeedGapReason.RootUnavailable),
        });

        Assert.Equal(@"boşluk: RootUnavailablex3 (C:\bir)", text);
    }

    [Fact]
    public void ManyReasons_AreOrderedByHowManyRootsTheyHit()
    {
        var text = UsnDrainRunner.DescribeGaps(new[]
        {
            Gap(@"C:\bir", ChangeFeedGapReason.NotYetSynchronized),
            Gap(@"C:\iki", ChangeFeedGapReason.RootIdentityChanged),
            Gap(@"C:\uc", ChangeFeedGapReason.RootIdentityChanged),
        });

        Assert.Equal(
            @"boşluk: RootIdentityChangedx2 (C:\iki), NotYetSynchronizedx1 (C:\bir)",
            text);
    }

    [Fact]
    public void AGapAmongHealthyRoots_IsStillNamed()
    {
        var text = UsnDrainRunner.DescribeGaps(new[]
        {
            Delivery(@"C:\saglam", ChangeFeedBatch.Ok(Array.Empty<ChangeFeedEvent>())),
            Gap(@"C:\bozuk", ChangeFeedGapReason.CursorOutsideJournal),
        });

        Assert.Equal(@"boşluk: CursorOutsideJournalx1 (C:\bozuk)", text);
    }

    [Theory]
    [InlineData(null, null, null)]
    [InlineData("arıza", null, "arıza")]
    [InlineData(null, "boşluk", "boşluk")]
    [InlineData("arıza", "boşluk", "arıza | boşluk")]
    public void FaultTextAndGapText_BothSurvive(
        string? primary,
        string? secondary,
        string? expected)
    {
        Assert.Equal(expected, UsnDrainRunner.Combine(primary, secondary));
    }

    [Fact]
    public void WithoutAGap_TheAnchorageIsNotReported()
    {
        Assert.Null(UsnDrainRunner.DescribeAnchorage(
            120,
            Journal(100, 200),
            gappedRoots: 0,
            reanchoredRoots: 0));
    }

    [Fact]
    public void AGap_ReportsTheCursorTheWindowAndTheReanchorCount()
    {
        Assert.Equal(
            "imleç=40 pencere=[100..200] yeniden-çapa=2/6",
            UsnDrainRunner.DescribeAnchorage(
                40,
                Journal(100, 200),
                gappedRoots: 6,
                reanchoredRoots: 2));
    }

    private static UsnJournalDescriptor Journal(long firstUsn, long nextUsn) =>
        new(1, firstUsn, nextUsn, 0, long.MaxValue, 0, 0);

    private static ChangeFeedRootDelivery Gap(string root, ChangeFeedGapReason reason) =>
        Delivery(root, ChangeFeedBatch.Gap(reason));

    private static ChangeFeedRootDelivery Delivery(string root, ChangeFeedBatch batch) =>
        new(root, batch, new ChangeFeedRootGeneration("g1"));
}
