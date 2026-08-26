using SmartFileLauncher.Core.Diagnostics;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Diagnostics;

public class DiagnosticsRateTrackerTests
{
    private static readonly DateTime Origin = new(2026, 8, 27, 10, 0, 0, DateTimeKind.Unspecified);

    [Fact]
    public void FirstObservationHasNoRate()
    {
        var tracker = new DiagnosticsRateTracker();

        Assert.Null(tracker.Update("a", 100d, Origin));
    }

    [Fact]
    public void ComputesRatePerSecond()
    {
        var tracker = new DiagnosticsRateTracker();
        tracker.Update("a", 100d, Origin);

        var rate = tracker.Update("a", 900d, Origin.AddSeconds(4));

        Assert.Equal(200d, rate);
    }

    [Fact]
    public void UsesElapsedSecondsNotSampleCount()
    {
        var tracker = new DiagnosticsRateTracker();
        tracker.Update("a", 0d, Origin);

        var rate = tracker.Update("a", 500d, Origin.AddSeconds(10));

        Assert.Equal(50d, rate);
    }

    [Fact]
    public void KeepsPreviousRateWhenIntervalTooShort()
    {
        var tracker = new DiagnosticsRateTracker(TimeSpan.FromSeconds(1));
        tracker.Update("a", 0d, Origin);
        tracker.Update("a", 100d, Origin.AddSeconds(2));

        var rate = tracker.Update("a", 1_000_000d, Origin.AddSeconds(2).AddMilliseconds(5));

        Assert.Equal(50d, rate);
    }

    [Fact]
    public void HasNoRateWhenIntervalTooShortBeforeFirstRate()
    {
        var tracker = new DiagnosticsRateTracker(TimeSpan.FromSeconds(1));
        tracker.Update("a", 0d, Origin);

        Assert.Null(tracker.Update("a", 100d, Origin.AddMilliseconds(5)));
    }

    [Fact]
    public void ResetsWhenCounterGoesBackwards()
    {
        var tracker = new DiagnosticsRateTracker();
        tracker.Update("a", 500d, Origin);

        Assert.Null(tracker.Update("a", 100d, Origin.AddSeconds(1)));
    }

    [Fact]
    public void ResumesAfterCounterReset()
    {
        var tracker = new DiagnosticsRateTracker();
        tracker.Update("a", 500d, Origin);
        tracker.Update("a", 100d, Origin.AddSeconds(1));

        var rate = tracker.Update("a", 400d, Origin.AddSeconds(4));

        Assert.Equal(100d, rate);
    }

    [Fact]
    public void ResetsWhenClockGoesBackwards()
    {
        var tracker = new DiagnosticsRateTracker();
        tracker.Update("a", 100d, Origin);

        Assert.Null(tracker.Update("a", 200d, Origin.AddSeconds(-5)));
    }

    [Fact]
    public void TracksKeysIndependently()
    {
        var tracker = new DiagnosticsRateTracker();
        tracker.Update("a", 0d, Origin);
        tracker.Update("b", 0d, Origin);

        var rateA = tracker.Update("a", 10d, Origin.AddSeconds(1));
        var rateB = tracker.Update("b", 40d, Origin.AddSeconds(1));

        Assert.Equal(10d, rateA);
        Assert.Equal(40d, rateB);
    }

    [Fact]
    public void ResetClearsState()
    {
        var tracker = new DiagnosticsRateTracker();
        tracker.Update("a", 0d, Origin);
        tracker.Reset();

        Assert.Null(tracker.Update("a", 100d, Origin.AddSeconds(1)));
    }

    [Fact]
    public void RejectsNonPositiveMinimumInterval()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new DiagnosticsRateTracker(TimeSpan.Zero));
    }
}
