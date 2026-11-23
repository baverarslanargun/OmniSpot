using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class SmoothScrollTests
{
    [Fact]
    public void AVisibleRowDoesNotScroll() =>
        Assert.Null(SmoothScroll.TargetOffset(top: 40, height: 230, viewport: 500, offset: 100, pending: 100));

    [Fact]
    public void ARowBelowTheViewportScrollsJustEnoughToShowItWithBreathingRoom() =>
        Assert.Equal(100 + 420 + 230 - 500 + 8, SmoothScroll.TargetOffset(top: 420, height: 230, viewport: 500, offset: 100, pending: 100));

    [Fact]
    public void ARowAboveTheViewportScrollsUpToItsTop() =>
        Assert.Equal(100 - 60 - 8, SmoothScroll.TargetOffset(top: -60, height: 230, viewport: 500, offset: 100, pending: 100));

    [Fact]
    public void HoldingTheKeyContinuesFromThePendingTargetInsteadOfTheAnimatedOffset()
    {
        var first = SmoothScroll.TargetOffset(top: 420, height: 230, viewport: 500, offset: 100, pending: 100)!.Value;
        var second = SmoothScroll.TargetOffset(top: 750 - 140, height: 230, viewport: 500, offset: 140, pending: first);
        Assert.Equal(first + 230, second);
    }

    [Fact]
    public void ARowTallerThanTheViewportAlignsToItsTop() =>
        Assert.Equal(100 + 200, SmoothScroll.TargetOffset(top: 200, height: 900, viewport: 500, offset: 100, pending: 100));
}
