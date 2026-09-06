using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Models;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class ChangeFeedIndexBridgeTests
{
    private const string Root = @"C:\Kok";

    [Fact]
    public async Task Adoption_SubscribesEveryRootBeforeItPulls()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root)));

        var result = await Bridge(channel).AdoptAsync(new[] { Root, @"C:\Diger" }, default);

        Assert.Equal(2, result.RootsSubscribed);
        Assert.Equal(
            new[]
            {
                ChangeFeedRequestKind.AddRoot,
                ChangeFeedRequestKind.AddRoot,
                ChangeFeedRequestKind.DrainAndHoldLease,
                ChangeFeedRequestKind.Pull
            },
            channel.Kinds);
    }

    [Fact]
    public async Task Adoption_TakesTheLeaseBeforeItPullsAnything()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(
            Page(Root, Event(ChangeFeedEventKind.Created, @"C:\Kok\a.txt")),
            receipt: "makbuz"));

        var result = await Bridge(channel).AdoptAsync(new[] { Root }, default);

        Assert.True(result.LeaseHeld);

        var lease = channel.Kinds.ToList()
            .IndexOf(ChangeFeedRequestKind.DrainAndHoldLease);
        var pull = channel.Kinds.ToList().IndexOf(ChangeFeedRequestKind.Pull);

        Assert.True(lease >= 0, "Kira isteği hiç gönderilmedi.");
        Assert.True(pull >= 0, "Teslim isteği hiç gönderilmedi.");
        Assert.True(
            lease < pull,
            "Kira ilk teslimden önce alınmalı; aksi halde servis tüketim sırasında " +
            "kuyruğa yazıp makbuzu geçersizleştirir.");
    }

    [Fact]
    public async Task Adoption_AppliesEveryEventBeforeItAcknowledges()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(
            Page(Root, Event(ChangeFeedEventKind.Created, @"C:\Kok\a.txt")),
            receipt: "makbuz"));

        var timeline = new List<string>();
        channel.Timeline = timeline;
        var target = new RecordingTarget { Timeline = timeline };
        var result = await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.Equal(ChangeFeedAdoptionStatus.Adopted, result.Status);
        Assert.Equal(1, result.EventsApplied);
        Assert.Equal(
            new[]
            {
                "istek:AddRoot", "istek:DrainAndHoldLease", "istek:Pull", "uygula",
                "istek:Acknowledge"
            },
            timeline);
    }

    [Fact]
    public async Task Adoption_FollowsAContinuationBeforeItAcknowledges()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(
            Page(Root, Event(ChangeFeedEventKind.Created, @"C:\Kok\a.txt")),
            continuation: "devam",
            hasMore: true));
        channel.Respond(Delivered(
            Page(Root, Event(ChangeFeedEventKind.Modified, @"C:\Kok\b.txt")),
            receipt: "makbuz"));

        var target = new RecordingTarget();
        var result = await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.Equal(2, result.PagesApplied);
        Assert.Equal(2, result.EventsApplied);
        Assert.Equal("devam", channel.Requests[3].Token);
        Assert.Contains(ChangeFeedRequestKind.DrainAndHoldLease, channel.Kinds);
        Assert.True(result.LeaseHeld);
    }

    [Fact]
    public async Task AGappedRoot_IsResynchronizedInsteadOfApplied()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(new ChangeFeedRootPageDto(
            Root,
            new[] { Event(ChangeFeedEventKind.Created, @"C:\Kok\a.txt") },
            ChangeFeedGapReason.CursorOutsideJournal,
            ChangeFeedFaultReason.None,
            false,
            false)));

        var target = new RecordingTarget();
        var result = await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.Equal(1, result.RootsResynchronized);
        Assert.Equal(0, result.EventsApplied);
        Assert.Empty(target.Applied);
        Assert.Equal(new[] { Root }, target.Resynchronized);
    }

    [Fact]
    public async Task AnAuthorizationGap_AlsoForcesResynchronization()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(new ChangeFeedRootPageDto(
            Root,
            Array.Empty<ChangeFeedEventDto>(),
            ChangeFeedGapReason.None,
            ChangeFeedFaultReason.None,
            AuthorizationGap: true,
            PayloadTooLarge: false)));

        var target = new RecordingTarget();
        await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.Equal(new[] { Root }, target.Resynchronized);
    }

    [Fact]
    public async Task AStaleChain_IsRestartedOnceAndThenAbandoned()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Failed(ChangeFeedResponseStatus.StaleChain));
        channel.Respond(Failed(ChangeFeedResponseStatus.StaleChain));

        var result = await Bridge(channel).AdoptAsync(new[] { Root }, default);

        Assert.Equal(
            new[]
            {
                ChangeFeedRequestKind.AddRoot,
                ChangeFeedRequestKind.DrainAndHoldLease,
                ChangeFeedRequestKind.Pull,
                ChangeFeedRequestKind.Pull,
                ChangeFeedRequestKind.ReleaseLease
            },
            channel.Kinds);
        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.False(result.LeaseHeld);
    }

    [Fact]
    public async Task AFailedApply_BlocksTheAcknowledgeAndReturnsTheLease()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(
            Page(Root, Event(ChangeFeedEventKind.Created, @"C:\Kok\a.txt")),
            receipt: "makbuz"));

        var target = new RecordingTarget
        {
            ApplySucceeds = false,
            ResyncSucceeds = false
        };
        var result = await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.False(result.LeaseHeld);
        Assert.DoesNotContain(ChangeFeedRequestKind.Acknowledge, channel.Kinds);
        Assert.Equal(ChangeFeedRequestKind.ReleaseLease, channel.Kinds[^1]);
    }

    [Fact]
    public async Task AFailedApply_IsReconciledBeforeThePageIsAcknowledged()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(
            Page(Root, Event(ChangeFeedEventKind.Created, @"C:\Kok\a.txt")),
            receipt: "makbuz"));

        var timeline = new List<string>();
        channel.Timeline = timeline;
        var target = new RecordingTarget
        {
            ApplySucceeds = false,
            ResyncSucceeds = true,
            Timeline = timeline
        };

        var result = await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.True(result.LeaseHeld);
        Assert.Equal(1, result.RootsResynchronized);
        Assert.Equal(
            new[]
            {
                "istek:AddRoot", "istek:DrainAndHoldLease", "istek:Pull", "uygula",
                "uzlaştır", "istek:Acknowledge"
            },
            timeline);
    }

    [Fact]
    public async Task AFailedResynchronization_BlocksTheAcknowledgeAndReturnsTheLease()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(
            new ChangeFeedRootPageDto(
                Root,
                Array.Empty<ChangeFeedEventDto>(),
                ChangeFeedGapReason.CursorOutsideJournal,
                ChangeFeedFaultReason.None,
                false,
                false),
            receipt: "makbuz"));

        var target = new RecordingTarget { ResyncSucceeds = false };
        var result = await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.False(result.LeaseHeld);
        Assert.DoesNotContain(ChangeFeedRequestKind.Acknowledge, channel.Kinds);
        Assert.Equal(ChangeFeedRequestKind.ReleaseLease, channel.Kinds[^1]);
    }

    [Fact]
    public async Task ARefusedAcknowledge_StopsTheAdoptionAndReturnsTheLease()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(
            Page(Root, Event(ChangeFeedEventKind.Created, @"C:\Kok\a.txt")),
            receipt: "makbuz"));
        channel.Respond(Failed(ChangeFeedResponseStatus.StaleChain));

        var result = await Bridge(channel).AdoptAsync(new[] { Root }, default);

        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.False(result.LeaseHeld);
        Assert.Contains("Onay reddedildi", result.Diagnostics);
        Assert.Equal(ChangeFeedRequestKind.ReleaseLease, channel.Kinds[^1]);
    }

    [Fact]
    public async Task NoPullHappensWhileTheServiceCanStillCommit()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(
            Page(Root, Event(ChangeFeedEventKind.Created, @"C:\Kok\gec.txt")),
            receipt: "makbuz"));

        var target = new RecordingTarget();
        var result = await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.Equal(ChangeFeedAdoptionStatus.Adopted, result.Status);
        Assert.True(result.LeaseHeld);
        Assert.Equal(1, result.EventsApplied);
        Assert.Equal(
            new[]
            {
                ChangeFeedRequestKind.AddRoot,
                ChangeFeedRequestKind.DrainAndHoldLease,
                ChangeFeedRequestKind.Pull,
                ChangeFeedRequestKind.Acknowledge
            },
            channel.Kinds);
    }

    [Fact]
    public async Task AFailedConsumption_HandsTheLeaseBack()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Failed(ChangeFeedResponseStatus.NoSubscription));

        var result = await Bridge(channel).AdoptAsync(new[] { Root }, default);

        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.False(result.LeaseHeld);
        Assert.Equal(
            new[]
            {
                ChangeFeedRequestKind.AddRoot,
                ChangeFeedRequestKind.DrainAndHoldLease,
                ChangeFeedRequestKind.Pull,
                ChangeFeedRequestKind.ReleaseLease
            },
            channel.Kinds);
    }

    [Fact]
    public async Task AnUnreachableService_LeavesTheIndexAloneAndTakesNoLease()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Throw();

        var target = new RecordingTarget();
        var result = await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.Equal(ChangeFeedAdoptionStatus.ServiceUnavailable, result.Status);
        Assert.False(result.LeaseHeld);
        Assert.Empty(target.Applied);
        Assert.DoesNotContain(ChangeFeedRequestKind.Pull, channel.Kinds);
        Assert.DoesNotContain(ChangeFeedRequestKind.ReleaseLease, channel.Kinds);
    }

    [Fact]
    public async Task ARefusedRoot_StopsAdoptionWithoutPulling()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Failed(ChangeFeedResponseStatus.RootUnauthorized));

        var result = await Bridge(channel).AdoptAsync(new[] { Root }, default);

        Assert.Equal(ChangeFeedAdoptionStatus.Refused, result.Status);
        Assert.Equal(new[] { ChangeFeedRequestKind.AddRoot }, channel.Kinds);
        Assert.Contains("RootUnauthorized", result.Diagnostics);
    }

    [Fact]
    public async Task HandOver_ReleasesTheLease()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());

        Assert.True(await Bridge(channel).HandOverAsync(default));
        Assert.Equal(new[] { ChangeFeedRequestKind.ReleaseLease }, channel.Kinds);
    }

    [Fact]
    public async Task RenewingALease_AsksForTheConfiguredDuration()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());

        var bridge = new ChangeFeedIndexBridge(
            channel,
            new RecordingTarget(),
            TimeSpan.FromSeconds(90));

        Assert.True(await bridge.RenewLeaseAsync(default));
        Assert.Equal(90, channel.Requests[0].LeaseSeconds);
    }

    [Fact]
    public void ALeaseLongerThanTheServiceAllows_IsRefusedAtConstruction()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ChangeFeedIndexBridge(
            new ScriptedChannel(),
            new RecordingTarget(),
            ChangeFeedWatcherLease.MaximumDuration + TimeSpan.FromSeconds(1)));
    }

    private static ChangeFeedIndexBridge Bridge(
        ScriptedChannel channel,
        IChangeFeedIndexTarget? target = null) =>
        new(channel, target ?? new RecordingTarget());

    private static ChangeFeedResponse Ok() => ChangeFeedResponse.Ok();

    private static ChangeFeedResponse Failed(ChangeFeedResponseStatus status) =>
        ChangeFeedResponse.Failed(status, status.ToString());

    private static ChangeFeedResponse Delivered(
        ChangeFeedRootPageDto root,
        string? continuation = null,
        string? receipt = null,
        bool hasMore = false) =>
        ChangeFeedResponse.Delivered(
            new ChangeFeedDeliveryDto(new[] { root }, hasMore, continuation, receipt));

    private static ChangeFeedRootPageDto Page(string root, params ChangeFeedEventDto[] events) =>
        new(
            root,
            events,
            ChangeFeedGapReason.None,
            ChangeFeedFaultReason.None,
            false,
            false);

    private static ChangeFeedEventDto Event(ChangeFeedEventKind kind, string path) =>
        new(kind, path, false, null);

    private sealed class ScriptedChannel : IChangeFeedRequestChannel
    {
        private readonly Queue<ChangeFeedResponse?> _scripted = new();

        public List<ChangeFeedRequest> Requests { get; } = new();

        public List<string>? Timeline { get; set; }

        public IReadOnlyList<ChangeFeedRequestKind> Kinds =>
            Requests.Select(request => request.Kind).ToArray();

        public void Respond(ChangeFeedResponse response) => _scripted.Enqueue(response);

        public void Throw() => _scripted.Enqueue(null);

        public Task<ChangeFeedResponse> SendAsync(
            ChangeFeedRequest request,
            CancellationToken cancellationToken)
        {
            Requests.Add(request);
            Timeline?.Add("istek:" + request.Kind);

            if (_scripted.Count == 0)
            {
                return Task.FromResult(request.Kind == ChangeFeedRequestKind.Pull
                    ? ChangeFeedResponse.Delivered(new ChangeFeedDeliveryDto(
                        Array.Empty<ChangeFeedRootPageDto>(),
                        false,
                        null,
                        null))
                    : ChangeFeedResponse.Ok());
            }

            var response = _scripted.Dequeue();
            return response is null
                ? throw new IOException("Test: boru kapalı.")
                : Task.FromResult(response);
        }
    }

    private sealed class RecordingTarget : IChangeFeedIndexTarget
    {
        public List<IReadOnlyList<FileChangeEvent>> Applied { get; } = new();

        public List<string> Resynchronized { get; } = new();

        public List<string>? Timeline { get; set; }

        public bool ApplySucceeds { get; set; } = true;

        public bool ResyncSucceeds { get; set; } = true;

        public bool Apply(IReadOnlyList<FileChangeEvent> changes)
        {
            Applied.Add(changes);
            Timeline?.Add("uygula");
            return ApplySucceeds;
        }

        public Task<bool> ResynchronizeAsync(
            string rootPath,
            bool withinLifecycle,
            CancellationToken cancellationToken)
        {
            Resynchronized.Add(rootPath);
            Timeline?.Add("uzlaştır");
            return Task.FromResult(ResyncSucceeds);
        }
    }
}
