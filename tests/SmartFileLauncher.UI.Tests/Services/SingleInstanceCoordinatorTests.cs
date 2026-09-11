using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class SingleInstanceCoordinatorTests
{
    [Fact]
    public void SecondInstanceSignalsPrimaryActivation()
    {
        var instanceId = Guid.NewGuid().ToString("N");
        using var activationReceived = new ManualResetEventSlim();
        using var primary = new SingleInstanceCoordinator(instanceId);
        using var secondary = new SingleInstanceCoordinator(instanceId);

        Assert.True(primary.IsPrimary);
        Assert.False(secondary.IsPrimary);

        primary.StartListening(
            activationReceived.Set,
            static () => { });

        secondary.SignalActivation();

        Assert.True(activationReceived.Wait(TimeSpan.FromSeconds(5)));
    }
}
