using SmartFileLauncher.Core.Application.Connectivity;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Connectivity;

public sealed class ConnectivityMonitorTests
{
    [Fact]
    public async Task CheckNowAsync_OnlyRaisesWhenStatusChanges()
    {
        var probeResults = new Queue<bool>([false, false, true]);
        using var monitor = new ConnectivityMonitor(
            _ => Task.FromResult(probeResults.Dequeue()),
            TimeSpan.FromMinutes(1));
        var changes = new List<bool>();
        monitor.ConnectivityChanged += changes.Add;

        await monitor.CheckNowAsync();
        await monitor.CheckNowAsync();
        await monitor.CheckNowAsync();

        Assert.Equal([false, true], changes);
        Assert.True(monitor.IsConnected);
    }

    [Fact]
    public async Task CheckNowAsync_ReportsProbeFailureAsDisconnected()
    {
        using var monitor = new ConnectivityMonitor(
            _ => throw new InvalidOperationException("probe failed"),
            TimeSpan.FromMinutes(1));

        var isConnected = await monitor.CheckNowAsync();

        Assert.False(isConnected);
    }

    [Fact]
    public async Task CheckNowAsync_PropagatesCancellation()
    {
        using var monitor = new ConnectivityMonitor(
            async cancellationToken => {
                await Task.Delay(
                    Timeout.InfiniteTimeSpan,
                    cancellationToken);
                return true;
            },
            TimeSpan.FromMinutes(1));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => monitor.CheckNowAsync(cancellation.Token));
    }
}
