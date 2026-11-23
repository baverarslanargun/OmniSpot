using System.Windows;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class SelectionGlideTests
{
    [Fact]
    public void TheRingTravelsFromTheOldCardToTheNewOne()
    {
        var from = new Rect(10, 20, 200, 230);
        var to = new Rect(241, 20, 200, 230);

        Assert.Equal(from, SelectionGlide.Interpolate(from, to, 0));
        Assert.Equal(to, SelectionGlide.Interpolate(from, to, 1));
        Assert.Equal(new Rect(125.5, 20, 200, 230), SelectionGlide.Interpolate(from, to, 0.5));
    }

    [Fact]
    public void TheMotionStartsFastAndSettlesAtTheTarget()
    {
        Assert.Equal(0, MotionTiming.Ease(0));
        Assert.Equal(1, MotionTiming.Ease(1));
        Assert.Equal(1, MotionTiming.Ease(1.4));
        Assert.True(MotionTiming.Ease(0.5) > 0.5);
        Assert.True(MotionTiming.Ease(0.25) < MotionTiming.Ease(0.5));
    }
}
