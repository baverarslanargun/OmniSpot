using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Models;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class ContinuousChangeFeedTests
{
    [Fact]
    public async Task EntireContinuationChainIsDurableBeforeAcknowledgementAndFailureKeepsReceipt()
    {
        var timeline = new List<string>(); var target = new Target(timeline); var channel = new Channel(timeline);
        var bridge = new ChangeFeedIndexBridge(channel, target);
        var result = await bridge.ConsumeContinuousAsync(CancellationToken.None);
        Assert.True(result.CaughtUp); Assert.Equal(2, result.Events);
        Assert.Equal(new[] { "Pull", "Pull", "commit:2", "Acknowledge" }, timeline);
        timeline.Clear(); channel.Reset(); target.Fail = true;
        await Assert.ThrowsAsync<IOException>(() => bridge.ConsumeContinuousAsync(CancellationToken.None));
        Assert.DoesNotContain("Acknowledge", timeline);
    }
    private sealed class Target(List<string> timeline) : IChangeFeedIndexTarget
    {
        internal bool Fail;
        public bool Apply(IReadOnlyList<FileChangeEvent> changes) => throw new NotSupportedException();
        public Task<bool> ResynchronizeAsync(string rootPath, bool withinLifecycle, CancellationToken cancellationToken) => throw new NotSupportedException();
        public Task CommitContinuousAsync(IReadOnlyList<ChangeFeedRootPageDto> roots, string deliveryId, CancellationToken cancellationToken)
        { timeline.Add("commit:" + roots.Sum(root => root.Events.Count)); if (Fail) throw new IOException("injected write failure"); return Task.CompletedTask; }
    }
    private sealed class Channel(List<string> timeline) : IChangeFeedRequestChannel
    {
        private int _pull;
        internal void Reset() => _pull = 0;
        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken cancellationToken)
        {
            timeline.Add(request.Kind.ToString());
            if (request.Kind == ChangeFeedRequestKind.Acknowledge) return Task.FromResult(ChangeFeedResponse.Ok());
            _pull++;
            var root = new ChangeFeedRootPageDto(@"C:\files", [new(ChangeFeedEventKind.Modified, @"C:\files\" + _pull + ".txt", false, null)], ChangeFeedGapReason.None, ChangeFeedFaultReason.None, false, false);
            return Task.FromResult(ChangeFeedResponse.Delivered(_pull == 1 ? new([root], true, "next", null)
                : new([root], false, null, "receipt", new string('A', 64), 5)));
        }
    }
}
