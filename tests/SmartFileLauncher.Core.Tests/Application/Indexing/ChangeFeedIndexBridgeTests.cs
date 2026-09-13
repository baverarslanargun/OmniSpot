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
    public async Task ScopeChangeRemovesObsoleteSubscriptionsBeforeTakingTheLease()
    {
        var channel = new ScriptedChannel();
        channel.Respond(ChangeFeedResponse.Ok([Root.ToLowerInvariant() + "\\", @"C:\Eski"]));
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root)));
        var result = await Bridge(channel).AdoptAsync([Root], default);
        Assert.True(result.LeaseHeld);
        Assert.Equal(new[] { ChangeFeedRequestKind.AddRoot, ChangeFeedRequestKind.RemoveRoot,
            ChangeFeedRequestKind.DrainAndHoldLease, ChangeFeedRequestKind.Pull }, channel.Kinds);
        Assert.Equal(@"C:\Eski", channel.Requests[1].RootPath);
    }

    [Fact]
    public async Task FailedSubscriptionDoesNotRemoveThePreviousScope()
    {
        var channel = new ScriptedChannel();
        channel.Respond(ChangeFeedResponse.Ok([Root, @"C:\Eski"]));
        channel.Respond(Failed(ChangeFeedResponseStatus.RootUnauthorized));
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root)));
        await Bridge(channel).AdoptAsync([Root, @"C:\Yeni"], default);
        Assert.DoesNotContain(ChangeFeedRequestKind.RemoveRoot, channel.Kinds);
    }

    [Fact]
    public async Task FailedObsoleteSubscriptionRemovalDoesNotTakeALeaseOrAcknowledge()
    {
        var channel = new ScriptedChannel();
        channel.Respond(ChangeFeedResponse.Ok([Root, @"C:\Eski"]));
        channel.Respond(Failed(ChangeFeedResponseStatus.Unavailable));
        var result = await Bridge(channel).AdoptAsync([Root], default);
        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.False(result.LeaseHeld);
        Assert.DoesNotContain(ChangeFeedRequestKind.DrainAndHoldLease, channel.Kinds);
        Assert.DoesNotContain(ChangeFeedRequestKind.Acknowledge, channel.Kinds);
    }

    [Fact]
    public async Task AValidatedInitialInventorySupersedesOldEventsAndProducerGaps()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root, Event(ChangeFeedEventKind.Deleted, @"C:\Kok\a.txt"))
            with { AuthorizationGap = true }, receipt: "makbuz"));
        var target = new RecordingTarget();
        var result = await Bridge(channel, target).AdoptAsync([Root], default, true, _ => Task.FromResult(true));
        Assert.True(result.LeaseHeld);
        Assert.Empty(target.Applied);
        Assert.Empty(target.Resynchronized);
        Assert.Contains(ChangeFeedRequestKind.Acknowledge, channel.Kinds);
    }

    [Fact]
    public async Task AFailedInventoryValidationUsesTheExistingResynchronization()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root) with { AuthorizationGap = true }, receipt: "makbuz"));
        var target = new RecordingTarget();
        await Bridge(channel, target).AdoptAsync([Root], default, true, _ => Task.FromResult(false));
        Assert.Equal([Root], target.Resynchronized);
        Assert.Contains(ChangeFeedRequestKind.Acknowledge, channel.Kinds);
    }

    [Fact]
    public async Task AWatcherGapBeforeAcknowledgementLeavesTheQueueAndReleasesTheLease()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root), receipt: "makbuz"));
        var validations = 0;
        var result = await Bridge(channel).AdoptAsync([Root], default, true,
            _ => Task.FromResult(++validations == 1));
        Assert.False(result.LeaseHeld);
        Assert.DoesNotContain(ChangeFeedRequestKind.Acknowledge, channel.Kinds);
        Assert.Contains(ChangeFeedRequestKind.ReleaseLease, channel.Kinds);
    }

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
    public async Task APartialHandOver_KeepsTheLeaseButClaimsNoCoverage()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(ChangeFeedResponse.Granted(
            "Son boşaltma bütçesi aşıldı; kira yine de verildi, devir eksik."));
        channel.Respond(Delivered(
            Page(Root, Event(ChangeFeedEventKind.Created, @"C:\Kok.txt")),
            receipt: "makbuz"));
        channel.Respond(Ok());

        var result = await Bridge(channel).AdoptAsync(new[] { Root }, default);

        Assert.True(
            result.LeaseHeld,
            "Kira alınmalı; toparlanmanın tek yolu kuyruğu tüketmek.");
        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.Contains("devir eksik", result.Diagnostics);
        Assert.False(
            IndexLifecycleService.CoversDowntime(result, 1),
            "Eksik devir kapsam saymaz: servisin okuyamadığı aralık ne boşluk " +
            "kaydına ne de watcher'a düştü; o açılışta tam tarama yapılmalı.");
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
    public async Task AFailedApply_IsDurablyDeferredBeforeThePageIsAcknowledged()
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
            DeferSucceeds = true,
            Timeline = timeline
        };

        var result = await Bridge(channel, target).AdoptAsync(new[] { Root }, default);

        Assert.True(result.LeaseHeld);
        Assert.Equal(0, result.RootsResynchronized);
        Assert.Empty(target.Resynchronized);
        Assert.Equal(@"C:\Kok\a.txt", Assert.Single(result.PendingRepairScopes!));
        Assert.Equal(result.PendingRepairScopes, target.Deferred);
        Assert.Equal(
            new[]
            {
                "istek:AddRoot", "istek:DrainAndHoldLease", "istek:Pull", "uygula",
                "kaydet", "istek:Acknowledge"
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

    [Fact]
    public async Task AuthorizationRecoveryAppliesVisibleEventsAndRepairsOnlyItsBoundariesBeforeAck()
    {
        var channel = new ScriptedChannel();
        var target = new RecordingTarget();
        var timeline = new List<string>();
        channel.Timeline = timeline;
        target.Timeline = timeline;
        var root = @"C:\Kok";
        var scopes = new[] { root + @"\Temp", root + @"\Other" };
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(root, Event(ChangeFeedEventKind.Modified, root + @"\visible.txt")) with {
            AuthorizationGap = true,
            AuthorizationScopesUtf16 = scopes.Select(ChangeFeedDeliveryContract.EncodeScope).ToArray()
        }, receipt: "receipt"));
        var result = await Bridge(channel, target).AdoptAsync([root], default);
        Assert.Equal(ChangeFeedAdoptionStatus.Adopted, result.Status);
        Assert.Single(target.Applied);
        Assert.Equal(scopes, target.Resynchronized);
        Assert.True(timeline.IndexOf("uygula") < timeline.IndexOf("uzlaştır"));
        Assert.True(timeline.LastIndexOf("uzlaştır") < timeline.IndexOf("istek:Acknowledge"));
    }

    [Theory]
    [InlineData(@"C:\Outside")]
    [InlineData(@"C:\KokSibling\outside")]
    [InlineData(@"relative\path")]
    public async Task AnInvalidBoundaryNeverEscapesTheSubscribedRoot(string scope)
    {
        var channel = new ScriptedChannel();
        var target = new RecordingTarget();
        var root = @"C:\Kok";
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(root) with { AuthorizationGap = true,
            AuthorizationScopesUtf16 = [ChangeFeedDeliveryContract.EncodeScope(scope)] }, receipt: "receipt"));
        await Bridge(channel, target).AdoptAsync([root], default);
        Assert.Equal(root, Assert.Single(target.Resynchronized));
    }

    [Fact]
    public async Task AFailedBoundaryRecoveryDoesNotAcknowledgeThePage()
    {
        var channel = new ScriptedChannel();
        var target = new RecordingTarget { ResyncSucceeds = false };
        var root = @"C:\Kok";
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(root) with { AuthorizationGap = true,
            AuthorizationScopesUtf16 = [ChangeFeedDeliveryContract.EncodeScope(root + @"\Closed")] }, receipt: "receipt"));
        var result = await Bridge(channel, target).AdoptAsync([root], default);
        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.DoesNotContain(ChangeFeedRequestKind.Acknowledge, channel.Kinds);
    }

    [Fact]
    public async Task ProducerGapsStillRequireTheWholeRootEvenWithBoundaries()
    {
        var channel = new ScriptedChannel();
        var target = new RecordingTarget();
        var root = @"C:\Kok";
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(root) with { AuthorizationGap = true,
            ProducerGap = ChangeFeedGapReason.DeliveryQueueOverflow,
            AuthorizationScopesUtf16 = [ChangeFeedDeliveryContract.EncodeScope(root + @"\Temp")] }));
        await Bridge(channel, target).AdoptAsync([root], default);
        Assert.Equal(root, Assert.Single(target.Resynchronized));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FailedKnownScopeIsAcknowledgedOnlyAfterDurableAcceptance(bool persist, bool throws)
    {
        var channel = new ScriptedChannel();
        var target = new RecordingTarget { ResyncSucceeds = false, DeferSucceeds = persist, ResyncThrows = throws };
        var scope = Root + @"\Closed";
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root) with { AuthorizationGap = true,
            AuthorizationScopesUtf16 = [ChangeFeedDeliveryContract.EncodeScope(scope)] }, receipt: "receipt"));
        var result = await Bridge(channel, target).AdoptAsync([Root], default);
        Assert.Equal(scope, Assert.Single(result.PendingRepairScopes!));
        Assert.Equal(scope, Assert.Single(target.Resynchronized));
        Assert.True(result.OnlyKnownRepairs);
        Assert.Equal(persist, channel.Kinds.Contains(ChangeFeedRequestKind.Acknowledge));
        Assert.Equal(persist ? ChangeFeedAdoptionStatus.Adopted : ChangeFeedAdoptionStatus.Incomplete, result.Status);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PendingScopesDoNotHideAnIncompleteServiceHandoff(bool persist)
    {
        var channel = new ScriptedChannel();
        var target = new RecordingTarget { ApplySucceeds = false, DeferSucceeds = persist };
        channel.Respond(Ok());
        channel.Respond(ChangeFeedResponse.Granted("Devir eksik."));
        channel.Respond(Delivered(Page(Root, Event(ChangeFeedEventKind.Modified, Root + @"\local.txt")), receipt: "receipt"));
        var result = await Bridge(channel, target).AdoptAsync([Root], default);
        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.NotEmpty(result.PendingRepairScopes!);
        Assert.False(result.OnlyKnownRepairs);
        Assert.False(IndexLifecycleService.CoversDowntime(result, 1));
    }

    [Fact]
    public async Task FailedAcknowledgementKeepsAlreadyPersistedRepairScopes()
    {
        var channel = new ScriptedChannel();
        var target = new RecordingTarget { ApplySucceeds = false, DeferSucceeds = true };
        var path = Root + @"\local.txt";
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root, Event(ChangeFeedEventKind.Modified, path)), receipt: "receipt"));
        channel.Respond(Failed(ChangeFeedResponseStatus.StaleChain));
        var result = await Bridge(channel, target).AdoptAsync([Root], default);
        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.Equal(path, Assert.Single(target.Deferred));
    }

    [Fact]
    public async Task AcknowledgedPrefixesContinueBeyondThePerChainPageLimit()
    {
        var channel = new ScriptedChannel();
        var target = new RecordingTarget();
        channel.Respond(Ok());
        channel.Respond(Ok());
        var count = ChangeFeedIndexBridge.MaximumPagesPerAdoption + 5;
        for (var index = 0; index < count; index++)
        {
            channel.Respond(Delivered(Page(Root, Event(ChangeFeedEventKind.Created, Root + "\\" + index + ".txt")),
                receipt: "receipt-" + index, hasMore: index != count - 1));
            channel.Respond(Ok());
        }
        var result = await Bridge(channel, target).AdoptAsync([Root], default);
        Assert.Equal(ChangeFeedAdoptionStatus.Adopted, result.Status);
        Assert.Equal(count, result.EventsApplied);
        Assert.Equal(count, channel.Kinds.Count(kind => kind == ChangeFeedRequestKind.Acknowledge));
        Assert.Empty(target.Resynchronized);
        Assert.All(channel.Requests.Where(request => request.Kind == ChangeFeedRequestKind.Pull), request => Assert.Null(request.Token));
    }

    [Fact]
    public async Task AChainWithoutAcknowledgableProgressRemainsBounded()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        for (var index = 0; index < ChangeFeedIndexBridge.MaximumPagesPerAdoption; index++)
            channel.Respond(Delivered(Page(Root), continuation: "next-" + index, hasMore: true));
        var result = await Bridge(channel).AdoptAsync([Root], default);
        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.False(result.LeaseHeld);
        Assert.Equal(ChangeFeedIndexBridge.MaximumPagesPerAdoption, channel.Kinds.Count(kind => kind == ChangeFeedRequestKind.Pull));
        Assert.DoesNotContain(ChangeFeedRequestKind.Acknowledge, channel.Kinds);
    }

    [Fact]
    public async Task EachAcknowledgedPrefixGetsItsOwnStaleChainRetry()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        for (var index = 0; index < 2; index++)
        {
            channel.Respond(Failed(ChangeFeedResponseStatus.StaleChain));
            channel.Respond(Delivered(Page(Root, Event(ChangeFeedEventKind.Modified, Root + "\\" + index + ".txt")),
                receipt: "receipt-" + index, hasMore: index == 0));
            channel.Respond(Ok());
        }
        var result = await Bridge(channel).AdoptAsync([Root], default);
        Assert.Equal(ChangeFeedAdoptionStatus.Adopted, result.Status);
        Assert.Equal(2, result.EventsApplied);
        Assert.Equal(2, channel.Kinds.Count(kind => kind == ChangeFeedRequestKind.Acknowledge));
        Assert.Equal(0, result.RootsResynchronized);
    }

    [Fact]
    public async Task MoreWithoutAReceiptOrContinuationIsNotCompleteCoverage()
    {
        var channel = new ScriptedChannel();
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root), hasMore: true));
        var result = await Bridge(channel).AdoptAsync([Root], default);
        Assert.Equal(ChangeFeedAdoptionStatus.Incomplete, result.Status);
        Assert.False(result.LeaseHeld);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LeaseIsRenewedWhileApplyingAndFailedRenewalPreventsAcknowledgement(bool succeeds)
    {
        var renewalObserved = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var releaseApply = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new ScriptedChannel { LeaseRenewal = _ =>
        {
            renewalObserved.TrySetResult();
            return Task.FromResult(succeeds ? Ok() : Failed(ChangeFeedResponseStatus.Unavailable));
        }};
        var target = new RecordingTarget { ApplyWait = releaseApply.Task };
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root, Event(ChangeFeedEventKind.Modified, Root + @"\local.txt")), receipt: "receipt"));
        var adoption = new ChangeFeedIndexBridge(channel, target, TimeSpan.FromSeconds(3)).AdoptAsync([Root], default);
        await renewalObserved.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (succeeds) releaseApply.TrySetResult();
        var result = await adoption.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(succeeds, result.LeaseHeld);
        Assert.Equal(succeeds, channel.Kinds.Contains(ChangeFeedRequestKind.Acknowledge));
        Assert.Contains(ChangeFeedRequestKind.HoldLease, channel.Kinds);
        if (!succeeds) Assert.Equal(ChangeFeedRequestKind.ReleaseLease, channel.Kinds[^1]);
    }

    [Fact]
    public async Task CancellationWaitsForAnInFlightRenewalBeforeReleasingTheLease()
    {
        var renewalStarted = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finishRenewal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applyCancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var channel = new ScriptedChannel { LeaseRenewal = async _ =>
        {
            renewalStarted.TrySetResult();
            await finishRenewal.Task;
            return Ok();
        }};
        var target = new RecordingTarget { ApplyWait = new TaskCompletionSource().Task, ApplyCancelled = applyCancelled };
        channel.Respond(Ok());
        channel.Respond(Ok());
        channel.Respond(Delivered(Page(Root, Event(ChangeFeedEventKind.Modified, Root + @"\local.txt")), receipt: "receipt"));
        using var cancellation = new CancellationTokenSource();
        var adoption = new ChangeFeedIndexBridge(channel, target, TimeSpan.FromSeconds(3)).AdoptAsync([Root], cancellation.Token);
        await renewalStarted.Task.WaitAsync(TimeSpan.FromSeconds(5));
        cancellation.Cancel();
        await applyCancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(adoption.IsCompleted);
        Assert.DoesNotContain(ChangeFeedRequestKind.ReleaseLease, channel.Kinds);
        finishRenewal.TrySetResult();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => adoption.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(ChangeFeedRequestKind.ReleaseLease, channel.Kinds[^1]);
        Assert.DoesNotContain(ChangeFeedRequestKind.Acknowledge, channel.Kinds);
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
        public Func<CancellationToken, Task<ChangeFeedResponse>>? LeaseRenewal { get; set; }

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
            if (request.Kind == ChangeFeedRequestKind.HoldLease && LeaseRenewal is not null)
                return LeaseRenewal(cancellationToken);

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
        public Task? ApplyWait { get; set; }
        public TaskCompletionSource? ApplyCancelled { get; set; }

        public async Task<IReadOnlyList<string>> ApplyOrRepairAsync(IReadOnlyList<FileChangeEvent> changes,
            bool withinLifecycle, CancellationToken ct)
        {
            var applied = Apply(changes);
            try { if (ApplyWait is not null) await ApplyWait.WaitAsync(ct); }
            catch (OperationCanceledException) { ApplyCancelled?.TrySetResult(); throw; }
            return applied ? [] : changes.SelectMany(change => change.OldPath is null
                ? new[] { change.FullPath } : new[] { change.FullPath, change.OldPath }).ToArray();
        }

        public bool ResyncSucceeds { get; set; } = true;
        public bool ResyncThrows { get; set; }

        public bool DeferSucceeds { get; set; }
        public List<string> Deferred { get; } = new();
        public bool DeferRepairs(IReadOnlyList<string> scopes)
        {
            Timeline?.Add("kaydet");
            if (DeferSucceeds) Deferred.AddRange(scopes);
            return DeferSucceeds;
        }

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
            if (ResyncThrows) throw new IOException("Test local failure");
            return Task.FromResult(ResyncSucceeds);
        }
    }
}
