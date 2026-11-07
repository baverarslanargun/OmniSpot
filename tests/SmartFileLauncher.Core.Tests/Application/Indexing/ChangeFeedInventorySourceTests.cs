using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class ChangeFeedInventorySourceTests
{
    [Fact]
    public async Task UnavailableBeforeSessionCreationCanRetryWithoutDuplicatingEntries()
    {
        var channel = new Channel(startFailure: true);
        var received = new List<IndexInventoryEntry>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        await using var session = await new ChangeFeedInventorySource(channel).ReadAsync([@"C:\root"], received.Add, deadline.Token);
        Assert.NotNull(session);
        Assert.Single(received);
        Assert.Equal(2, channel.InventoryRequests);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UnavailableAfterSessionCreationFailsAndCancelsEvenAnEmptyStream(bool empty)
    {
        var channel = new Channel(startFailure: false, empty);
        var received = new List<IndexInventoryEntry>();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        Assert.Null(await new ChangeFeedInventorySource(channel).ReadAsync([@"C:\root"], received.Add, deadline.Token));
        Assert.Equal(empty ? 0 : 1, received.Count);
        Assert.Equal(2, channel.InventoryRequests);
        Assert.Equal("session", channel.CancelledToken);
    }

    private sealed class Channel(bool startFailure, bool empty = false) : IChangeFeedRequestChannel
    {
        internal int InventoryRequests;
        internal string? CancelledToken;
        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken cancellationToken)
        {
            if (request.Kind == ChangeFeedRequestKind.CancelInventory) CancelledToken = request.Token;
            if (request.Kind != ChangeFeedRequestKind.Inventory) return Task.FromResult(ChangeFeedResponse.Ok());
            var attempt = ++InventoryRequests;
            if (attempt == (startFailure ? 1 : 2))
                return Task.FromResult(ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, "injected"));
            var entry = ChangeFeedInventoryEntryDto.FromEntry(new(@"C:\root\file", false, FileAttributes.Normal, 1, 1, 2));
            return Task.FromResult(new ChangeFeedResponse(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok,
                Inventory: new("session", empty ? [] : [entry], Completed: startFailure)));
        }
    }
}
