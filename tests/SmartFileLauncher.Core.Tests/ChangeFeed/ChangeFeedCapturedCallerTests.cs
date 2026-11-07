using System.IO.Pipes;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

[System.Runtime.Versioning.SupportedOSPlatform("windows")]
public sealed class ChangeFeedCapturedCallerTests
{
    [Fact]
    public async Task CapturedCallerSurvivesDisconnectAndAlwaysReverts()
    {
        using var current = WindowsIdentity.GetCurrent();
        using var captured = await CaptureAsync();
        Assert.Equal(current.User, captured.Run(sid => sid));
        Assert.Throws<IOException>(() => captured.Run<int>(_ => throw new IOException("expected")));
        ChangeFeedCallerIdentity.EnsureNotImpersonating();
        captured.Dispose();
        Assert.Throws<ObjectDisposedException>(() => captured.Run(sid => sid));
    }

    internal static async Task<ChangeFeedCaller> CaptureAsync()
    {
        var name = "OmniSpot.CapturedCaller." + Guid.NewGuid().ToString("N");
        using var server = new NamedPipeServerStream(name, PipeDirection.InOut, 1, PipeTransmissionMode.Byte, PipeOptions.Asynchronous);
        using var client = new NamedPipeClientStream(".", name, PipeDirection.InOut, PipeOptions.Asynchronous, TokenImpersonationLevel.Impersonation);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(15));
        var accepting = server.WaitForConnectionAsync(deadline.Token);
        await client.ConnectAsync(deadline.Token);
        await accepting;
        var received = ChangeFeedMessageChannel.ReadRequestAsync<ChangeFeedRequest>(server, deadline.Token);
        await ChangeFeedMessageChannel.WriteRequestAsync(client,
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.GetCapabilities), deadline.Token);
        await received;
        return ChangeFeedCaller.FromPipe(server).Capture();
    }
}
