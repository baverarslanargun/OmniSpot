using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class ThumbnailMemoryPolicyTests
{
    private const long GiB = 1024L * 1024 * 1024;

    [Fact]
    public void ManualHugeCountIsBoundedByPhysicalAvailableAndAbsoluteBudget()
    {
        var settings = new AppSettings { ThumbnailCacheMaxCount = int.MaxValue };
        var budget = ThumbnailMemoryPolicy.Calculate(settings, new(64 * GiB, 48 * GiB, 240 * 1024 * 1024));
        Assert.Equal(512L * 1024 * 1024, budget.Bytes);
        Assert.Equal(8192, budget.Count);
        var constrained = ThumbnailMemoryPolicy.Calculate(settings, new(8 * GiB, GiB / 2, 0));
        Assert.True(constrained.Bytes <= GiB / 20);
        Assert.True(constrained.Bytes <= 8 * GiB / 50);
    }

    [Fact]
    public void RatioUsesTotalPhysicalRamAndDoesNotChangeWithOtherUsageBelowSafetyCap()
    {
        var settings = new AppSettings { ThumbnailCacheUseRamRatio = true, ThumbnailCacheRamPercent = 0.1 };
        var first = ThumbnailMemoryPolicy.Calculate(settings, new(64 * GiB, 48 * GiB, 0));
        var second = ThumbnailMemoryPolicy.Calculate(settings, new(64 * GiB, 24 * GiB, 0));
        Assert.Equal((long)(64 * GiB * 0.001d), first.Bytes);
        Assert.Equal(first.Bytes, second.Bytes);
    }

    [Theory]
    [InlineData(double.NaN)]
    [InlineData(double.PositiveInfinity)]
    [InlineData(-10d)]
    public void InvalidRatioCannotAllocate(double percent)
    {
        var budget = ThumbnailMemoryPolicy.Calculate(new() { ThumbnailCacheUseRamRatio = true, ThumbnailCacheRamPercent = percent },
            new(64 * GiB, 48 * GiB, 0));
        Assert.Equal(0, budget.Bytes);
    }

    [Fact]
    public void MissingMemoryInformationAndDisabledPreviewsFailClosed()
    {
        Assert.Equal(0, ThumbnailMemoryPolicy.Calculate(new(), default).Bytes);
        Assert.Equal(0, ThumbnailMemoryPolicy.Calculate(new() { ThumbnailPreviewsEnabled = false }, new(64 * GiB, 48 * GiB, 0)).Bytes);
    }

    [Fact]
    public void PinnedFolderMatchingRespectsBoundariesAndWindowsCasing()
    {
        var folders = new[] { @"C:\Images" };
        Assert.True(ThumbnailMemoryPolicy.IsPinned(@"c:\images\Trips\one.png", folders));
        Assert.False(ThumbnailMemoryPolicy.IsPinned(@"C:\Images-other\one.png", folders));
        Assert.False(ThumbnailMemoryPolicy.IsPinned(@"C:\Images\..\Other\one.png", folders));
    }

    [Fact]
    public void IdleTransitionHappensOnceAndInputRestartsTheDeadline()
    {
        var now = TimeSpan.Zero;
        var activity = new ThumbnailActivityTracker(() => now);
        now = TimeSpan.FromSeconds(59);
        Assert.False(activity.Check(TimeSpan.FromSeconds(60)));
        Assert.False(activity.RecordActivity());
        now = TimeSpan.FromSeconds(118);
        Assert.False(activity.Check(TimeSpan.FromSeconds(60)));
        now = TimeSpan.FromSeconds(119);
        Assert.True(activity.Check(TimeSpan.FromSeconds(60)));
        now = TimeSpan.FromHours(1);
        Assert.False(activity.Check(TimeSpan.FromSeconds(60)));
        Assert.True(activity.RecordActivity());
        Assert.False(activity.IsIdle);
    }
}
