using SmartFileLauncher.Core.Application.Refresh;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Refresh;

public sealed class RefreshCoalescerTests
{
    [Fact]
    public void TryBegin_RequiresPendingRequest()
    {
        var coalescer = new RefreshCoalescer();

        Assert.False(coalescer.TryBegin());
    }

    [Fact]
    public void MultiplePendingRequests_StartSingleRefresh()
    {
        var coalescer = new RefreshCoalescer();
        coalescer.Request();
        coalescer.Request();

        Assert.True(coalescer.TryBegin());
        Assert.False(coalescer.TryBegin());
        Assert.False(coalescer.Complete());
    }

    [Fact]
    public void RequestDuringRefresh_RemainsPendingAfterCompletion()
    {
        var coalescer = new RefreshCoalescer();
        coalescer.Request();
        Assert.True(coalescer.TryBegin());

        coalescer.Request();

        Assert.True(coalescer.Complete());
        Assert.True(coalescer.TryBegin());
        Assert.False(coalescer.Complete());
    }
}
