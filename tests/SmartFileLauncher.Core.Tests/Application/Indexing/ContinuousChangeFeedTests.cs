using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Models;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class ContinuousChangeFeedTests
{
    [Fact]
    public async Task FixedBoundaryCompletesWhileLaterEventsRemainForTheNextCycle()
    {
        var timeline = new List<string>();
        var channel = new AppendingChannel(timeline);
        var bridge = new ChangeFeedIndexBridge(channel, new Target(timeline));

        var result = await bridge.ConsumeContinuousAsync(CancellationToken.None, throughSequence: 3);

        Assert.True(result.Available);
        Assert.True(result.CaughtUp);
        Assert.Equal(3, result.Events);
        Assert.Equal(3, channel.Acknowledged);
        Assert.Equal(9, timeline.Count);
        Assert.Equal(new[] { "Pull", "commit:1", "Acknowledge" }, timeline.Take(3));

        var next = await bridge.ConsumeContinuousAsync(CancellationToken.None, throughSequence: 4);
        Assert.True(next.CaughtUp);
        Assert.Equal(1, next.Events);
        Assert.Equal(4, channel.Acknowledged);
    }

    [Fact]
    public async Task FixedBoundarySurvivesThePerCyclePageBudget()
    {
        var timeline = new List<string>();
        var channel = new AppendingChannel(timeline);
        var bridge = new ChangeFeedIndexBridge(channel, new Target(timeline));

        var first = await bridge.ConsumeContinuousAsync(CancellationToken.None, throughSequence: 513);
        Assert.True(first.Available);
        Assert.False(first.CaughtUp);
        Assert.Equal(512, channel.Acknowledged);

        var last = await bridge.ConsumeContinuousAsync(CancellationToken.None, throughSequence: 513);
        Assert.True(last.CaughtUp);
        Assert.Equal(513, channel.Acknowledged);
        Assert.Equal(1, last.Events);
    }

    [Fact]
    public async Task EmptyBoundaryDoesNotWaitForNewEvents()
    {
        var timeline = new List<string>();
        var bridge = new ChangeFeedIndexBridge(new AppendingChannel(timeline), new Target(timeline));

        var result = await bridge.ConsumeContinuousAsync(CancellationToken.None, throughSequence: 0);

        Assert.True(result.CaughtUp);
        Assert.Empty(timeline);
    }

    [Fact]
    public async Task FixedBoundaryIsNotCompleteWhenAcknowledgementFails()
    {
        var timeline = new List<string>();
        var channel = new AppendingChannel(timeline) { RejectAcknowledgement = true };
        var bridge = new ChangeFeedIndexBridge(channel, new Target(timeline));

        var result = await bridge.ConsumeContinuousAsync(CancellationToken.None, throughSequence: 1);

        Assert.False(result.Available);
        Assert.False(result.CaughtUp);
        Assert.True(result.Retryable);
        Assert.Equal(0, channel.Acknowledged);
    }

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

    private sealed class AppendingChannel(List<string> timeline) : IChangeFeedRequestChannel
    {
        private long _sequence;
        internal long Acknowledged;
        internal bool RejectAcknowledgement;
        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken cancellationToken)
        {
            timeline.Add(request.Kind.ToString());
            if (request.Kind == ChangeFeedRequestKind.Acknowledge)
            {
                if (RejectAcknowledgement) return Task.FromResult(ChangeFeedResponse.Failed(ChangeFeedResponseStatus.StaleChain, "changed"));
                Acknowledged = _sequence;
                return Task.FromResult(ChangeFeedResponse.Ok());
            }
            _sequence++;
            var root = new ChangeFeedRootPageDto(@"C:\files", [new(ChangeFeedEventKind.Modified,
                @"C:\files\" + _sequence + ".txt", false, null)], ChangeFeedGapReason.None, ChangeFeedFaultReason.None, false, false);
            return Task.FromResult(ChangeFeedResponse.Delivered(new([root], true, null, "receipt",
                _sequence.ToString("X64"), _sequence)));
        }
    }
}
