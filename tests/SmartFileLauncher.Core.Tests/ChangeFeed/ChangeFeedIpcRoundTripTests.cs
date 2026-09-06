using System.ComponentModel;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedIpcRoundTripTests
{
    [Fact]
    public async Task AddRoot_AdmitsAListableRootAndStoresItInTheTrustedStore()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
        Assert.Equal(new[] { root }, response.Roots);
        Assert.Equal(new[] { root }, harness.StoredRoots());
    }

    [Fact]
    public async Task AddRoot_IsIdempotentForTheSameCanonicalPath()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        var again = await harness.SendAsync(
            new ChangeFeedRequest(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.AddRoot,
                root + Path.DirectorySeparatorChar));

        Assert.Equal(ChangeFeedResponseStatus.Ok, again.Status);
        Assert.Single(harness.StoredRoots());
    }

    [Fact]
    public async Task ConcurrentAdmissions_LoseNoRootToAReadModifyWriteRace()
    {
        using var harness = new Harness();
        var roots = Enumerable
            .Range(0, 4)
            .Select(index => harness.Workspace.CreateDirectory($"Kok{index}"))
            .ToArray();

        for (var round = 0; round < 8; round++)
        {
            foreach (var root in roots)
            {
                await harness.SendAsync(new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.RemoveRoot,
                    root));
            }

            var responses = await Task.WhenAll(roots.Select(root => Task.Run(() =>
                harness.SendAsync(new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.AddRoot,
                    root)))));

            Assert.All(responses, response =>
                Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status));

            Assert.Equal(
                roots.Length,
                harness.StoredRoots().Count);
        }
    }

    [Fact]
    public async Task RealAdmissionAddRoot_IsSerializedAgainstAContendingStore()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");
        var order = new List<string>();

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var blocker = Task.Run(() =>
        {
            using var scope = harness.OwnerStore().EnterOwnerScope();
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
            lock (order)
            {
                order.Add("rakip");
            }
        });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Rakip kapıyı tutamadı.");

        var admission = Task.Run(async () =>
        {
            var response = await harness.SendAsync(
                new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.AddRoot,
                    root));

            lock (order)
            {
                order.Add("kabul");
            }

            return response;
        });

        var raced = await Task.WhenAny(admission, Task.Delay(TimeSpan.FromMilliseconds(400)));
        Assert.NotSame(admission, raced);
        Assert.False(
            File.Exists(harness.SubscriptionPath()),
            "Kabul yolu kapı tutulurken abonelik yazdı.");

        release.Set();

        var result = await admission;
        await blocker;

        Assert.Equal(ChangeFeedResponseStatus.Ok, result.Status);
        Assert.Equal(new[] { "rakip", "kabul" }, order);
        Assert.Single(harness.StoredRoots());
    }

    [Fact]
    public async Task RealAdmissionRemoveRoot_IsSerializedAgainstAContendingStore()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();

        var blocker = Task.Run(() =>
        {
            using var scope = harness.OwnerStore().EnterOwnerScope();
            entered.Set();
            release.Wait(TimeSpan.FromSeconds(5));
        });

        Assert.True(entered.Wait(TimeSpan.FromSeconds(5)), "Rakip kapıyı tutamadı.");

        var removal = Task.Run(() => harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.RemoveRoot, root)));

        var raced = await Task.WhenAny(removal, Task.Delay(TimeSpan.FromMilliseconds(400)));
        Assert.NotSame(removal, raced);
        Assert.True(
            File.Exists(harness.SubscriptionPath()),
            "Kaldırma kapı tutulurken aboneliği sildi.");

        release.Set();
        await removal;
        await blocker;

        Assert.Empty(harness.StoredRoots());
    }

    [Fact]
    public async Task Client_RefusesAServerSpeakingAnotherProtocolVersion()
    {
        var pipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");
        using var server = ChangeFeedPipeFactory.Create(pipeName, true, CurrentSid());

        var serving = Task.Run(async () =>
        {
            await server.WaitForConnectionAsync(CancellationToken.None);
            await ChangeFeedMessageChannel.ReadRequestAsync<ChangeFeedRequest>(
                server,
                CancellationToken.None);
            await ChangeFeedMessageChannel.WriteResponseAsync(
                server,
                new ChangeFeedResponse(
                    ChangeFeedProtocol.Version - 1,
                    ChangeFeedResponseStatus.Ok),
                CancellationToken.None);
        });

        var client = new ChangeFeedClient(
            pipeName,
            new HashSet<SecurityIdentifier> { CurrentSid() });

        var response = await client.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
            CancellationToken.None);

        await serving;

        Assert.Equal(ChangeFeedResponseStatus.VersionMismatch, response.Status);
    }

    [Fact]
    public async Task Pull_DeliversTheBacklogOverTheRealPipeAndAckDrainsIt()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");
        var file = System.IO.Path.Combine(root, "yeni.txt");
        File.WriteAllText(file, "x");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        Enqueue(harness, file);

        var pull = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.Pull));

        Assert.Equal(ChangeFeedResponseStatus.Ok, pull.Status);
        var page = Assert.Single(pull.Delivery!.Roots);
        Assert.Equal(file, Assert.Single(page.Events).Path);
        Assert.NotNull(pull.Delivery.Receipt);
        Assert.Null(pull.Delivery.Continuation);
        Assert.NotEmpty(harness.OwnerStore().ReadPending().Entries);

        var ack = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.Acknowledge,
            null,
            pull.Delivery.Receipt));

        Assert.Equal(ChangeFeedResponseStatus.Ok, ack.Status);
        Assert.Empty(harness.OwnerStore().ReadPending().Entries);
    }

    [Fact]
    public async Task PullAndAck_TouchTheTrustedStoreOutsideTheCallersToken()
    {
        using var harness = new Harness(guardImpersonation: true);
        var root = harness.Workspace.CreateDirectory("Projeler");
        var file = System.IO.Path.Combine(root, "yeni.txt");
        File.WriteAllText(file, "x");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        Enqueue(harness, file);

        var pull = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.Pull));

        Assert.Equal(ChangeFeedResponseStatus.Ok, pull.Status);
        Assert.Equal(file, Assert.Single(Assert.Single(pull.Delivery!.Roots).Events).Path);

        var ack = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.Acknowledge,
            null,
            pull.Delivery.Receipt));

        Assert.Equal(ChangeFeedResponseStatus.Ok, ack.Status);
        Assert.Empty(harness.OwnerStore().ReadPending().Entries);
        Assert.Empty(harness.ImpersonatedStoreCalls);
    }

    [Fact]
    public async Task Pull_DecidesPathAccessUnderTheCallersOwnToken()
    {
        using var harness = new Harness(guardImpersonation: true, observeAuthorization: true);
        var root = harness.Workspace.CreateDirectory("Projeler");
        var child = Directory.CreateDirectory(System.IO.Path.Combine(root, "Alt")).FullName;
        var file = System.IO.Path.Combine(child, "yeni.txt");
        File.WriteAllText(file, "x");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        Enqueue(harness, file);

        var pull = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.Pull));

        Assert.Equal(ChangeFeedResponseStatus.Ok, pull.Status);
        Assert.Equal(file, Assert.Single(Assert.Single(pull.Delivery!.Roots).Events).Path);
        Assert.Empty(harness.ImpersonatedStoreCalls);

        var decisions = harness.AuthorizationContexts;
        Assert.NotEmpty(decisions);
        Assert.All(decisions, sid => Assert.Equal(CurrentSid(), sid));
    }

    [Theory]
    [InlineData(ChangeFeedRequestKind.AddRoot)]
    [InlineData(ChangeFeedRequestKind.RemoveRoot)]
    [InlineData(ChangeFeedRequestKind.ListRoots)]
    [InlineData(ChangeFeedRequestKind.Pull)]
    [InlineData(ChangeFeedRequestKind.Acknowledge)]
    [InlineData(ChangeFeedRequestKind.HoldLease)]
    [InlineData(ChangeFeedRequestKind.DrainAndHoldLease)]
    [InlineData(ChangeFeedRequestKind.ReleaseLease)]
    public async Task EveryRequestKind_ReachesAHandler(ChangeFeedRequestKind kind)
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        var response = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            kind,
            root,
            new string('a', ChangeFeedDeliveryLedger.TokenLength),
            LeaseSeconds: 60));

        Assert.NotEqual("Bilinmeyen istek türü.", response.Message);
        Assert.Null(harness.ListenerFault);
    }

    [Fact]
    public async Task HoldLease_LandsInTheCallersOwnStoreAndReleaseClearsIt()
    {
        using var harness = new Harness();

        var held = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.HoldLease,
            LeaseSeconds: 120));

        Assert.Equal(ChangeFeedResponseStatus.Ok, held.Status);
        Assert.True(harness.OwnerStore().ReadLease().IsHeld(DateTime.UtcNow));

        var released = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.ReleaseLease));

        Assert.Equal(ChangeFeedResponseStatus.Ok, released.Status);
        Assert.Equal(ChangeFeedWatcherLease.None, harness.OwnerStore().ReadLease());
        Assert.Null(harness.ListenerFault);
    }

    [Fact]
    public async Task DrainAndHoldLease_HoldsTheLeaseOnlyAfterTheFinalDrainSucceeds()
    {
        using var harness = new Harness(handoffDrainer: (_, _) => true);

        var response = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.DrainAndHoldLease,
            LeaseSeconds: 120));

        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
        Assert.Equal(1, harness.HandoffDrainCount);
        Assert.True(harness.OwnerStore().ReadLease().IsHeld(DateTime.UtcNow));
    }

    [Fact]
    public async Task DrainAndHoldLease_AbandonsTheLeaseWhenTheFinalDrainOutlivesItsBudget()
    {
        using var harness = new Harness(
            handoffDrainer: (_, cancellationToken) =>
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                cancellationToken.ThrowIfCancellationRequested();
                return true;
            },
            handoffDrainBudget: TimeSpan.FromMilliseconds(200));

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        var response = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.DrainAndHoldLease,
            LeaseSeconds: 120));
        elapsed.Stop();

        Assert.Equal(ChangeFeedResponseStatus.Unavailable, response.Status);
        Assert.Contains("bütçesi", response.Message);
        Assert.Equal(ChangeFeedWatcherLease.None, harness.OwnerStore().ReadLease());
        Assert.True(
            elapsed.Elapsed < ChangeFeedProtocol.IoTimeout,
            $"Son boşaltma {elapsed.Elapsed.TotalSeconds:F1} saniye tuttu; IPC bütçesini aşıyor.");
    }

    [Fact]
    public void TheHandoffDrainBudget_StaysInsideTheIoBudget()
    {
        Assert.True(ChangeFeedProtocol.HandoffDrainBudget > TimeSpan.Zero);
        Assert.True(ChangeFeedProtocol.HandoffDrainBudget < ChangeFeedProtocol.IoTimeout);
    }

    [Fact]
    public async Task DrainAndHoldLease_DoesNotHoldTheLeaseWhenTheFinalDrainFails()
    {
        using var harness = new Harness(handoffDrainer: (_, _) => false);

        var response = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.DrainAndHoldLease,
            LeaseSeconds: 120));

        Assert.Equal(ChangeFeedResponseStatus.Unavailable, response.Status);
        Assert.Equal(1, harness.HandoffDrainCount);
        Assert.Equal(ChangeFeedWatcherLease.None, harness.OwnerStore().ReadLease());
    }

    [Fact]
    public async Task DrainAndHoldLease_PreemptsAStaleLeaseBeforeTheFinalDrain()
    {
        Harness? current = null;
        var leaseWasHeldDuringDrain = false;
        using var harness = new Harness(handoffDrainer: (_, _) =>
        {
            leaseWasHeldDuringDrain = current!.OwnerStore()
                .ReadLease()
                .IsHeld(DateTime.UtcNow);
            return !leaseWasHeldDuringDrain;
        });
        current = harness;

        Assert.Equal(
            ChangeFeedResponseStatus.Ok,
            (await harness.SendAsync(new ChangeFeedRequest(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.HoldLease,
                LeaseSeconds: 120))).Status);

        var response = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.DrainAndHoldLease,
            LeaseSeconds: 120));

        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
        Assert.False(leaseWasHeldDuringDrain);
        Assert.True(harness.OwnerStore().ReadLease().IsHeld(DateTime.UtcNow));
    }

    [Fact]
    public async Task DrainAndHoldLease_HoldsTheOwnerGateAcrossTheFinalDrainAndLeaseWrite()
    {
        using var drainEntered = new ManualResetEventSlim();
        using var releaseDrain = new ManualResetEventSlim();
        using var contenderEntered = new ManualResetEventSlim();
        using var harness = new Harness(handoffDrainer: (_, cancellationToken) =>
        {
            drainEntered.Set();
            releaseDrain.Wait(cancellationToken);
            return true;
        });

        var handoff = harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.DrainAndHoldLease,
            LeaseSeconds: 120));

        Assert.True(drainEntered.Wait(TimeSpan.FromSeconds(5)));

        var contender = Task.Run(() =>
        {
            using (harness.OwnerStore().EnterOwnerScope())
            {
                contenderEntered.Set();
            }
        });

        Assert.False(
            contenderEntered.Wait(TimeSpan.FromMilliseconds(200)),
            "Son drain sürerken aynı sahip deposu kilidi alınabildi.");

        releaseDrain.Set();
        Assert.Equal(ChangeFeedResponseStatus.Ok, (await handoff).Status);
        await contender.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(contenderEntered.IsSet);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-30)]
    public async Task HoldLease_WithANonPositiveDurationIsRefusedAndWritesNothing(int seconds)
    {
        using var harness = new Harness();

        var response = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.HoldLease,
            LeaseSeconds: seconds));

        Assert.Equal(ChangeFeedResponseStatus.InvalidRequest, response.Status);
        Assert.Equal(ChangeFeedWatcherLease.None, harness.OwnerStore().ReadLease());
    }

    [Fact]
    public async Task HoldLease_BeyondTheMaximumIsRefusedInsteadOfSilentlyCapped()
    {
        using var harness = new Harness();
        var seconds = (int)ChangeFeedWatcherLease.MaximumDuration.TotalSeconds;

        var accepted = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.HoldLease,
            LeaseSeconds: seconds));

        Assert.Equal(ChangeFeedResponseStatus.Ok, accepted.Status);

        await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.ReleaseLease));

        var refused = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.HoldLease,
            LeaseSeconds: seconds + 1));

        Assert.Equal(ChangeFeedResponseStatus.InvalidRequest, refused.Status);
        Assert.Equal(ChangeFeedWatcherLease.None, harness.OwnerStore().ReadLease());
    }

    [Fact]
    public async Task LeaseRequests_AreServedOutsideImpersonation()
    {
        using var harness = new Harness(guardImpersonation: true);

        await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.HoldLease,
            LeaseSeconds: 120));
        await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.DrainAndHoldLease,
            LeaseSeconds: 120));
        await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.ReleaseLease));

        Assert.Empty(harness.ImpersonatedStoreCalls);
        Assert.All(harness.HandoffDrainContexts, identity => Assert.Null(identity));
    }

    [Fact]
    public async Task AHeldLease_DoesNotBlockPullOrAcknowledge()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.AddRoot,
            root));
        await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.HoldLease,
            LeaseSeconds: 120));

        var pull = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.Pull));

        Assert.Equal(ChangeFeedResponseStatus.Ok, pull.Status);
        Assert.NotNull(pull.Delivery);
    }

    [Fact]
    public async Task Pull_WithoutASubscriptionIsRefusedWithADefinedStatus()
    {
        using var harness = new Harness();

        var pull = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.Pull));

        Assert.Equal(ChangeFeedResponseStatus.NoSubscription, pull.Status);
        Assert.Null(pull.Delivery);
    }

    [Fact]
    public async Task Acknowledge_WithAnInventedTokenIsRefusedAndDeletesNothing()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");
        var file = System.IO.Path.Combine(root, "yeni.txt");
        File.WriteAllText(file, "x");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        Enqueue(harness, file);

        var ack = await harness.SendAsync(new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.Acknowledge,
            null,
            new string('a', 64)));

        Assert.Equal(ChangeFeedResponseStatus.StaleChain, ack.Status);
        Assert.NotEmpty(harness.OwnerStore().ReadPending().Entries);
    }

    private static void Enqueue(Harness harness, string file)
    {
        var store = harness.OwnerStore();
        var subscribed = store.ReadSubscription()!.Roots[0];

        store.Enqueue(
            "ntfs-vsn:0x000000000000ABCD",
            7,
            100,
            200,
            new[]
            {
                new ChangeFeedRootDelivery(
                    subscribed.RootPath,
                    ChangeFeedBatch.Ok(new[]
                    {
                        new ChangeFeedEvent(ChangeFeedEventKind.Created, file, false)
                    }),
                    subscribed.Generation)
            });
    }

    [Fact]
    public async Task AddRoot_RefusesToGrowTheSubscriptionBeyondItsRootCeiling()
    {
        using var harness = new Harness();
        var full = Enumerable
            .Range(0, ChangeFeedSubscription.MaximumRoots)
            .Select(index => new ChangeFeedSubscribedRoot(
                @"C:\Dolu" + index,
                new ChangeFeedRootIdentity("vol-1", "node-" + index),
                ChangeFeedRootGeneration.New()))
            .ToArray();

        harness.OwnerStore().WriteSubscription(
            new ChangeFeedSubscription(CurrentSid().Value, full));

        var root = harness.Workspace.CreateDirectory("Fazla");

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        Assert.Equal(ChangeFeedResponseStatus.InvalidRequest, response.Status);
        Assert.Equal(ChangeFeedSubscription.MaximumRoots, harness.StoredRoots().Count);
        Assert.DoesNotContain(root, harness.StoredRoots());
    }

    [Fact]
    public async Task AddRoot_KeepsTheGenerationWhenTheSameRootIsAdmittedAgain()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));
        var first = Assert.Single(harness.StoredSubscription()).Generation;

        await harness.SendAsync(
            new ChangeFeedRequest(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.AddRoot,
                root + Path.DirectorySeparatorChar));

        Assert.False(first.IsUnknown);
        Assert.Equal(first, Assert.Single(harness.StoredSubscription()).Generation);
    }

    [Fact]
    public async Task AddRoot_RenewsTheGenerationAfterARealRemoval()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));
        var first = Assert.Single(harness.StoredSubscription()).Generation;

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.RemoveRoot, root));
        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        var second = Assert.Single(harness.StoredSubscription()).Generation;

        Assert.False(second.IsUnknown);
        Assert.NotEqual(first, second);
    }

    [Fact]
    public async Task RemoveRoot_IsIdempotentAndClearsTheSubscriptionWhenEmpty()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        var removed = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.RemoveRoot, root));
        Assert.Equal(ChangeFeedResponseStatus.Ok, removed.Status);
        Assert.Empty(harness.StoredRoots());

        var again = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.RemoveRoot, root));
        Assert.Equal(ChangeFeedResponseStatus.Ok, again.Status);
        Assert.Empty(again.Roots!);
    }

    [Fact]
    public async Task AddRoot_RefusesARootThatCannotBeUsed()
    {
        using var harness = new Harness();

        var response = await harness.SendAsync(
            new ChangeFeedRequest(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.AddRoot,
                Path.Combine(harness.Workspace.Path, "yok")));

        Assert.Equal(ChangeFeedResponseStatus.RootUnusable, response.Status);
        Assert.Empty(harness.StoredRoots());
    }

    [Fact]
    public async Task AddRoot_RefusesARootTheCallerIsDeniedByAcl()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Kapali");
        Deny(root);

        try
        {
            var response = await harness.SendAsync(
                new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.AddRoot,
                    root));

            Assert.Equal(ChangeFeedResponseStatus.RootUnauthorized, response.Status);
            Assert.Empty(harness.StoredRoots());
        }
        finally
        {
            Undeny(root);
        }
    }

    [Fact]
    public async Task AddRoot_ReportsUnauthorizedWhenTheCallerCannotReadThePathChain()
    {
        using var harness = new Harness();
        var parent = harness.Workspace.CreateDirectory("KapaliUst");
        var root = Directory.CreateDirectory(Path.Combine(parent, "Icerik")).FullName;
        Deny(parent);

        try
        {
            var response = await harness.SendAsync(
                new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.AddRoot,
                    root));

            Assert.Equal(ChangeFeedResponseStatus.RootUnauthorized, response.Status);
            Assert.Empty(harness.StoredRoots());
        }
        finally
        {
            Undeny(parent);
        }
    }

    [Fact]
    public async Task AddRoot_AcceptsARootWhoseChildIsClosedToTheCaller()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Acik");
        var child = Directory.CreateDirectory(Path.Combine(root, "Kapali")).FullName;
        Deny(child);

        try
        {
            var response = await harness.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

            Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
            Assert.Equal(new[] { root }, harness.StoredRoots());
        }
        finally
        {
            Undeny(child);
        }
    }

    [Fact]
    public async Task ImpersonationFailure_AdmitsNothingAndTouchesNoTrustedStore()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();
        var root = harness.Workspace.CreateDirectory("Projeler");

        using var weak = ChangeFeedPipeFactory.Connect(
            harness.PipeName,
            TokenImpersonationLevel.Identification);

        await ChangeFeedMessageChannel.WriteRequestAsync(
            weak,
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root),
            CancellationToken.None);

        var response = await ChangeFeedMessageChannel.ReadResponseAsync<ChangeFeedResponse>(
            weak,
            CancellationToken.None);

        Assert.Equal(ChangeFeedResponseStatus.RootUnauthorized, response.Status);
        Assert.False(Directory.Exists(harness.OwnerDirectory()));
    }

    [Fact]
    public async Task RejectionText_NamesNothingBeyondThePathTheCallerSent()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Kapali");
        Deny(root);

        try
        {
            var response = await harness.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

            Assert.Equal(ChangeFeedResponseStatus.RootUnauthorized, response.Status);

            var message = response.Message!;
            Assert.Contains(root, message, StringComparison.Ordinal);
            Assert.DoesNotContain(harness.TrustedRoot, message, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                ChangeFeedStoreLayout.DefaultTrustedRoot,
                message,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(CurrentSid().Value, message, StringComparison.OrdinalIgnoreCase);

            var extra = message
                .Replace(root, string.Empty, StringComparison.Ordinal)
                .Split(new[] { ' ', '(', ')', ':' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(token => token.Contains(Path.DirectorySeparatorChar))
                .ToArray();

            Assert.Empty(extra);
        }
        finally
        {
            Undeny(root);
        }
    }

    [Fact]
    public async Task Request_WithAnotherVersionIsRefusedWithoutDowngrade()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version + 1, ChangeFeedRequestKind.AddRoot, root));

        Assert.Equal(ChangeFeedResponseStatus.VersionMismatch, response.Status);
        Assert.Equal(ChangeFeedProtocol.Version, response.Version);
        Assert.Empty(harness.StoredRoots());
    }

    [Fact]
    public async Task Request_OverTheSizeLimitIsRefusedOnARealConnection()
    {
        using var harness = new Harness();

        var oversized = new ChangeFeedRequest(
            ChangeFeedProtocol.Version,
            ChangeFeedRequestKind.AddRoot,
            new string('k', ChangeFeedProtocol.MaximumRequestBytes));

        await Assert.ThrowsAsync<ChangeFeedProtocolException>(
            () => harness.Client.SendAsync(oversized, CancellationToken.None));

        Assert.Empty(harness.StoredRoots());

        var afterwards = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots));
        Assert.Equal(ChangeFeedResponseStatus.Ok, afterwards.Status);
    }

    [Fact]
    public async Task Server_RefusesAnOversizedFrameAndClosesTheConnection()
    {
        using var harness = new Harness();

        using var raw = ChangeFeedPipeFactory.Connect(
            harness.PipeName,
            TokenImpersonationLevel.Impersonation);

        await raw.WriteAsync(BitConverter.GetBytes(ChangeFeedProtocol.MaximumRequestBytes + 1));
        await raw.FlushAsync();

        var response = await ChangeFeedMessageChannel.ReadResponseAsync<ChangeFeedResponse>(
            raw,
            CancellationToken.None);

        Assert.Equal(ChangeFeedResponseStatus.InvalidRequest, response.Status);

        var trailing = new byte[1];
        Assert.Equal(0, await raw.ReadAsync(trailing));
        Assert.Empty(harness.StoredRoots());
    }

    [Fact]
    public void Request_CarriesNoIdentityFieldTheCallerCouldClaim()
    {
        var members = typeof(ChangeFeedRequest)
            .GetProperties()
            .Select(property => property.Name)
            .ToArray();

        Assert.Equal(
            new[] { "Version", "Kind", "RootPath", "Token", "LeaseSeconds" },
            members);
    }

    [Fact]
    public async Task Client_RefusesAServerItDoesNotTrust()
    {
        using var harness = new Harness();
        var client = new ChangeFeedClient(harness.PipeName, ChangeFeedClient.DefaultTrustedOwners());

        var failure = await Assert.ThrowsAsync<ChangeFeedUntrustedServerException>(
            () => client.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
                CancellationToken.None));

        Assert.Contains("güvenilir değil", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task UntrustedServer_IsNeverSoftenedIntoUnavailable()
    {
        using var harness = new Harness();
        var client = new ChangeFeedClient(harness.PipeName, ChangeFeedClient.DefaultTrustedOwners());

        var failure = await Record.ExceptionAsync(
            () => client.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
                CancellationToken.None));

        Assert.IsType<ChangeFeedUntrustedServerException>(failure);
    }

    [Fact]
    public async Task Client_ReportsUnavailableWhenNobodyIsListening()
    {
        var client = new ChangeFeedClient("OmniSpot.Test." + Guid.NewGuid().ToString("N"));

        var response = await client.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
            CancellationToken.None);

        Assert.Equal(ChangeFeedResponseStatus.Unavailable, response.Status);
    }

    [Fact]
    public async Task Listener_RefusesToStartWhenThePipeNameIsAlreadyTaken()
    {
        using var harness = new Harness(listen: false);
        using var squatter = ChangeFeedPipeFactory.CreateFirstInstance(harness.PipeName);

        var failure = await Assert.ThrowsAsync<ChangeFeedPipeException>(
            () => harness.Server.ListenAsync(CancellationToken.None));

        Assert.Contains("zaten var", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Listener_RefusesTheOverflowClientWhileEverySlotIsHeld()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            var overflow = Assert.Throws<ChangeFeedPipeException>(
                () => ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation,
                    busyWait: TimeSpan.Zero));

            Assert.Equal(231, ((Win32Exception)overflow.InnerException!).NativeErrorCode);
        }
        finally
        {
            foreach (var client in silent)
            {
                client.Dispose();
            }
        }

        await harness.ReadyAsync();
    }

    [Fact]
    public async Task Listener_ReclaimsASlotHeldByASilentClientOnlyAfterTheTimeout()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        using var silent = ChangeFeedPipeFactory.Connect(
            harness.PipeName,
            TokenImpersonationLevel.Impersonation);

        await WaitUntilAsync(
            () => harness.Server.AvailableSlots == ChangeFeedProtocol.MaximumConcurrentConnections - 1,
            TimeSpan.FromSeconds(5));

        await Task.Delay(ChangeFeedProtocol.IoTimeout - TimeSpan.FromSeconds(2));
        Assert.Equal(
            ChangeFeedProtocol.MaximumConcurrentConnections - 1,
            harness.Server.AvailableSlots);

        await WaitUntilAsync(
            () => harness.Server.AvailableSlots == ChangeFeedProtocol.MaximumConcurrentConnections,
            TimeSpan.FromSeconds(15));

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots));
        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
    }

    [Fact]
    public async Task Listener_StopsAsAUnitWhenOneInstanceOfThePoolCannotBeCreated()
    {
        using var workspace = new TemporaryDirectory();
        var pipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");
        var trustedRoot = Path.Combine(workspace.Path, "Guvenilir");

        var service = new ChangeFeedAdmissionService(
            new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
            ownerSid => new FileSystemChangeFeedStore(
                ChangeFeedStoreLayout.ForTrustedOwner(trustedRoot, ownerSid, CurrentSid().Value)));

        var created = new List<System.IO.Pipes.NamedPipeServerStream>();
        var server = new ChangeFeedPipeServer(
            service,
            pipeName,
            CurrentSid(),
            null,
            firstInstance =>
            {
                if (created.Count == 2)
                {
                    throw new ChangeFeedPipeException("Havuz instance'ı kurulamadı.");
                }

                var pipe = ChangeFeedPipeFactory.Create(pipeName, firstInstance, CurrentSid());
                created.Add(pipe);
                return pipe;
            });

        var failure = await Assert.ThrowsAsync<ChangeFeedPipeException>(
            () => server.ListenAsync(CancellationToken.None));

        Assert.Contains("Havuz instance'ı kurulamadı", failure.Message, StringComparison.Ordinal);
        Assert.Equal(2, created.Count);
        Assert.Equal(0, server.AvailableSlots);

        var orphan = Assert.Throws<ChangeFeedPipeException>(
            () => ChangeFeedPipeFactory.Connect(
                pipeName,
                TokenImpersonationLevel.Impersonation,
                busyWait: TimeSpan.Zero));

        Assert.Equal(2, ((Win32Exception)orphan.InnerException!).NativeErrorCode);
    }

    [Fact]
    public async Task Listener_StopsAsAUnitWhenAReplacementInstanceCannotBeCreated()
    {
        using var workspace = new TemporaryDirectory();
        var pipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");
        var trustedRoot = Path.Combine(workspace.Path, "Guvenilir");

        var service = new ChangeFeedAdmissionService(
            new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
            ownerSid => new FileSystemChangeFeedStore(
                ChangeFeedStoreLayout.ForTrustedOwner(trustedRoot, ownerSid, CurrentSid().Value)));

        var created = 0;
        var server = new ChangeFeedPipeServer(
            service,
            pipeName,
            CurrentSid(),
            null,
            firstInstance =>
            {
                if (Interlocked.Increment(ref created) > ChangeFeedProtocol.MaximumConcurrentConnections)
                {
                    throw new ChangeFeedPipeException("Yerine geçen instance kurulamadı.");
                }

                return ChangeFeedPipeFactory.Create(pipeName, firstInstance, CurrentSid());
            });

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var listening = server.ListenAsync(stop.Token);

        await WaitUntilAsync(
            () => server.AvailableSlots == ChangeFeedProtocol.MaximumConcurrentConnections,
            TimeSpan.FromSeconds(10));

        var client = new ChangeFeedClient(
            pipeName,
            new HashSet<SecurityIdentifier> { CurrentSid() });

        var response = await client.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
            CancellationToken.None);
        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);

        var failure = await Assert.ThrowsAsync<ChangeFeedPipeException>(() => listening);
        Assert.Contains("Yerine geçen instance kurulamadı", failure.Message, StringComparison.Ordinal);
        Assert.Equal(0, server.AvailableSlots);

        var orphan = Assert.Throws<ChangeFeedPipeException>(
            () => ChangeFeedPipeFactory.Connect(
                pipeName,
                TokenImpersonationLevel.Impersonation,
                busyWait: TimeSpan.Zero));

        Assert.Equal(2, ((Win32Exception)orphan.InnerException!).NativeErrorCode);
    }

    [Fact]
    public async Task Listener_NeverLeavesTheNameUnservedWhileEveryWorkerRecyclesAtOnce()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();
        var unserved = 0;
        var observed = 0;
        var probing = true;
        Task? probe = null;

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            probe = Task.Run(() =>
            {
                while (Volatile.Read(ref probing))
                {
                    try
                    {
                        using var attempt = ChangeFeedPipeFactory.Connect(
                            harness.PipeName,
                            TokenImpersonationLevel.Impersonation,
                            busyWait: TimeSpan.Zero);
                    }
                    catch (ChangeFeedPipeException failure)
                        when (failure.InnerException is Win32Exception win32)
                    {
                        Interlocked.Increment(ref observed);

                        if (win32.NativeErrorCode == 2)
                        {
                            Interlocked.Increment(ref unserved);
                        }
                    }
                }
            });

            await Task.Delay(ChangeFeedProtocol.IoTimeout + TimeSpan.FromSeconds(2));
        }
        finally
        {
            Volatile.Write(ref probing, false);

            if (probe is not null)
            {
                await probe;
            }

            foreach (var client in silent)
            {
                client.Dispose();
            }
        }

        Assert.Null(harness.ListenerFault);
        Assert.True(observed > 0, "Prob hiç red gözlemedi; ölçüm anlamsız.");
        Assert.Equal(0, unserved);
        await harness.ReadyAsync();
    }

    [Fact]
    public async Task Listener_StopsAsAUnitWhenItCannotAcceptAtAll()
    {
        using var workspace = new TemporaryDirectory();
        var pipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");
        var trustedRoot = Path.Combine(workspace.Path, "Guvenilir");

        var service = new ChangeFeedAdmissionService(
            new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
            ownerSid => new FileSystemChangeFeedStore(
                ChangeFeedStoreLayout.ForTrustedOwner(trustedRoot, ownerSid, CurrentSid().Value)));

        var server = new ChangeFeedPipeServer(
            service,
            pipeName,
            CurrentSid(),
            null,
            firstInstance =>
            {
                var pipe = ChangeFeedPipeFactory.Create(pipeName, firstInstance, CurrentSid());
                pipe.Dispose();
                return pipe;
            });

        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(30));

        await Assert.ThrowsAsync<ObjectDisposedException>(() => server.ListenAsync(stop.Token));

        Assert.Equal(0, server.AvailableSlots);
    }

    [Fact]
    public async Task Listener_SurvivesAClientThatConnectsAndVanishesRepeatedly()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        for (var i = 0; i < 200; i++)
        {
            ChangeFeedPipeFactory.Connect(
                harness.PipeName,
                TokenImpersonationLevel.Impersonation).Dispose();
        }

        Assert.Null(harness.ListenerFault);
        await harness.ReadyAsync();

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots));
        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
    }

    [Fact]
    public async Task Listener_KeepsEveryWorkerWhenTheFaultObserverItselfThrows()
    {
        using var harness = new Harness(faultObserverThrows: true);
        await harness.ReadyAsync();

        using (var abandoned = ChangeFeedPipeFactory.Connect(
            harness.PipeName,
            TokenImpersonationLevel.Impersonation))
        {
            await WaitUntilAsync(
                () => harness.Server.AvailableSlots
                    < ChangeFeedProtocol.MaximumConcurrentConnections,
                TimeSpan.FromSeconds(5));
        }

        for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections * 4; i++)
        {
            ChangeFeedPipeFactory.Connect(
                harness.PipeName,
                TokenImpersonationLevel.Impersonation).Dispose();
        }

        await WaitUntilAsync(
            () => harness.FaultCount >= ChangeFeedProtocol.MaximumConcurrentConnections,
            TimeSpan.FromSeconds(10));

        Assert.Null(harness.ListenerFault);
        await harness.ReadyAsync();

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots));
        Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
    }

    [Fact]
    public async Task Client_IsServedByAReplacementInstanceWithinTheDefaultBudget()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            var clock = System.Diagnostics.Stopwatch.StartNew();
            var pending = Task.Run(() => harness.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots)));

            await Task.Delay(TimeSpan.FromMilliseconds(300));
            Assert.False(pending.IsCompleted, "İstemci dolu sunucuda beklemeden sonuçlandı.");

            silent[0].Dispose();
            silent.RemoveAt(0);

            var response = await pending;
            clock.Stop();

            Assert.Equal(ChangeFeedResponseStatus.Ok, response.Status);
            Assert.True(
                clock.Elapsed < ChangeFeedProtocol.IoTimeout,
                $"Yanıt bütçe dolduktan sonra geldi: {clock.Elapsed}.");
        }
        finally
        {
            foreach (var client in silent)
            {
                client.Dispose();
            }
        }

        await harness.ReadyAsync();
    }

    [Fact]
    public async Task Client_HonoursCancellationWhileEverySlotIsHeld()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            using var cancel = new CancellationTokenSource(TimeSpan.FromMilliseconds(100));
            var started = DateTime.UtcNow;

            await Assert.ThrowsAnyAsync<OperationCanceledException>(
                () => harness.Client.SendAsync(
                    new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots),
                    cancel.Token));

            var waited = DateTime.UtcNow - started;
            Assert.True(
                waited < ChangeFeedProtocol.IoTimeout,
                $"İptal {waited.TotalMilliseconds:0} ms sürdü, IoTimeout'tan kısa olmalıydı.");

            Assert.Empty(harness.StoredRoots());
        }
        finally
        {
            foreach (var client in silent)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public void Client_WaitsExactlyTheProtocolBudgetByDefault()
    {
        Assert.Equal(ChangeFeedProtocol.IoTimeout, ChangeFeedClient.DefaultBusyWait);
        Assert.Equal(
            ChangeFeedProtocol.IoTimeout,
            new ChangeFeedClient(ChangeFeedProtocol.PipeName).BusyWait);
    }

    [Fact]
    public async Task Client_FailsClosedWhenTheWaitBudgetRunsOutWhileEverySlotIsHeld()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();

        var budget = TimeSpan.FromSeconds(1);
        var impatient = new ChangeFeedClient(
            harness.PipeName,
            new HashSet<SecurityIdentifier> { CurrentSid() },
            budget);

        var silent = new List<System.IO.Pipes.NamedPipeClientStream>();

        try
        {
            for (var i = 0; i < ChangeFeedProtocol.MaximumConcurrentConnections; i++)
            {
                silent.Add(ChangeFeedPipeFactory.Connect(
                    harness.PipeName,
                    TokenImpersonationLevel.Impersonation));
            }

            await WaitUntilAsync(() => harness.Server.AvailableSlots == 0, TimeSpan.FromSeconds(5));

            var root = harness.Workspace.CreateDirectory("Projeler");

            var started = DateTime.UtcNow;
            var response = await impatient.SendAsync(
                new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root),
                CancellationToken.None);
            var waited = DateTime.UtcNow - started;

            Assert.Equal(ChangeFeedResponseStatus.Unavailable, response.Status);
            Assert.False(Directory.Exists(harness.OwnerDirectory()));
            Assert.True(
                waited >= budget - TimeSpan.FromMilliseconds(300),
                $"Bütçe dolmadan reddedildi: {waited.TotalMilliseconds:0} ms.");
            Assert.True(
                waited < ChangeFeedProtocol.IoTimeout,
                $"Bütçeden çok sonra reddedildi: {waited.TotalMilliseconds:0} ms.");
            Assert.Equal(0, harness.Server.AvailableSlots);
            Assert.Empty(harness.StoredRoots());
        }
        finally
        {
            foreach (var client in silent)
            {
                client.Dispose();
            }
        }
    }

    [Fact]
    public async Task Subscription_SurvivesConcurrentAddRemoveAndReadsWithoutLoss()
    {
        using var harness = new Harness();
        var kept = harness.Workspace.CreateDirectory("Kalici");
        var churned = harness.Workspace.CreateDirectory("Degisken");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, kept));

        var stop = new CancellationTokenSource();
        var readerFaults = new List<Exception>();

        var readers = Enumerable.Range(0, 3).Select(_ => Task.Run(() =>
        {
            while (!stop.IsCancellationRequested)
            {
                try
                {
                    var roots = harness.StoredRoots();
                    if (!roots.Contains(kept))
                    {
                        lock (readerFaults)
                        {
                            readerFaults.Add(new InvalidOperationException("Kalıcı kök snapshot'tan düştü."));
                        }
                    }
                }
                catch (Exception failure)
                {
                    lock (readerFaults)
                    {
                        readerFaults.Add(failure);
                    }
                }
            }
        })).ToArray();

        var drainFaults = new List<Exception>();
        var draining = Task.Run(() =>
        {
            var layout = harness.LayoutForCurrentUser();
            var runner = new UsnDrainRunner(
                layout,
                new FileSystemChangeFeedStore(layout),
                new UsnVolumeJournalReaderFactory(),
                new UsnFileSystemIdentityProbe());

            while (!stop.IsCancellationRequested)
            {
                try
                {
                    runner.Run(stop.Token);
                }
                catch (OperationCanceledException)
                {
                }
                catch (Exception failure)
                {
                    lock (drainFaults)
                    {
                        drainFaults.Add(failure);
                    }
                }
            }
        });

        var clientFaults = new List<string>();

        var mutators = Enumerable.Range(0, 3).Select(lane => Task.Run(async () =>
        {
            var mine = lane == 0 ? churned : harness.Workspace.CreateDirectory("Degisken" + lane);

            for (var i = 0; i < 20; i++)
            {
                foreach (var kind in new[]
                         {
                             ChangeFeedRequestKind.AddRoot,
                             ChangeFeedRequestKind.RemoveRoot
                         })
                {
                    try
                    {
                        var response = await harness.SendAsync(
                            new ChangeFeedRequest(ChangeFeedProtocol.Version, kind, mine));

                        if (response.Status != ChangeFeedResponseStatus.Ok)
                        {
                            lock (clientFaults)
                            {
                                clientFaults.Add($"{kind} -> {response.Status} {response.Message}");
                            }
                        }
                    }
                    catch (Exception failure)
                    {
                        lock (clientFaults)
                        {
                            clientFaults.Add($"{kind} -> {failure.Message}");
                        }
                    }
                }
            }
        })).ToArray();

        await Task.WhenAll(mutators);

        stop.Cancel();
        await Task.WhenAll(readers);
        await draining;

        Assert.Empty(drainFaults.Select(failure => failure.ToString()).ToArray());
        Assert.Empty(harness.Faults.Select(failure => failure.ToString()).ToArray());
        Assert.Empty(clientFaults);
        Assert.Empty(readerFaults);
        Assert.Equal(new[] { kept }, harness.StoredRoots());
    }

    [Fact]
    public async Task CancelledRequests_LeaveNoHalfWrittenAdmissionRecord()
    {
        using var harness = new Harness();
        await harness.ReadyAsync();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        for (var attempt = 0; attempt < 40; attempt++)
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromTicks(attempt * 2000));
            var kind = attempt % 2 == 0
                ? ChangeFeedRequestKind.AddRoot
                : ChangeFeedRequestKind.RemoveRoot;

            try
            {
                await harness.SendAsync(
                    new ChangeFeedRequest(ChangeFeedProtocol.Version, kind, root),
                    cancel.Token);
            }
            catch (OperationCanceledException)
            {
            }

            var store = new FileSystemChangeFeedStore(harness.LayoutForCurrentUser());

            ChangeFeedSubscription? state = null;
            var read = Record.Exception(() => state = store.ReadSubscription());
            Assert.Null(read);

            var roots = state?.Roots.Select(entry => entry.RootPath).ToArray()
                ?? Array.Empty<string>();

            Assert.True(
                roots.Length == 0 || (roots.Length == 1 && roots[0] == root),
                $"Ara durum beklenmedik: [{string.Join(", ", roots)}]");
        }

        Assert.Empty(Directory.GetFiles(
            harness.OwnerDirectory(),
            "*.tmp",
            SearchOption.AllDirectories));
    }

    [Fact]
    public async Task CorruptSubscription_FailsClosedInsteadOfBeingSilentlyReplaced()
    {
        using var harness = new Harness();
        var root = harness.Workspace.CreateDirectory("Projeler");

        await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        var subscription = harness.SubscriptionPath();
        File.WriteAllText(subscription, "{ bozuk");

        var response = await harness.SendAsync(
            new ChangeFeedRequest(ChangeFeedProtocol.Version, ChangeFeedRequestKind.AddRoot, root));

        Assert.Equal(ChangeFeedResponseStatus.Unavailable, response.Status);
        Assert.Equal("{ bozuk", File.ReadAllText(subscription));
    }

    private static async Task WaitUntilAsync(Func<bool> condition, TimeSpan limit)
    {
        var deadline = DateTime.UtcNow + limit;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(25);
        }

        Assert.True(condition(), "Beklenen durum süresi içinde oluşmadı.");
    }

    private static void Deny(string path)
    {
        var directory = new DirectoryInfo(path);
        var access = directory.GetAccessControl();
        access.SetAccessRuleProtection(true, false);
        access.AddAccessRule(new FileSystemAccessRule(
            CurrentSid(),
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            InheritanceFlags.None,
            PropagationFlags.None,
            AccessControlType.Deny));
        directory.SetAccessControl(access);
    }

    private static void Undeny(string path)
    {
        var directory = new DirectoryInfo(path);
        var access = directory.GetAccessControl();
        access.SetAccessRuleProtection(false, true);
        access.RemoveAccessRuleAll(new FileSystemAccessRule(
            CurrentSid(),
            FileSystemRights.ListDirectory | FileSystemRights.ReadData,
            AccessControlType.Deny));
        directory.SetAccessControl(access);
    }

    private static SecurityIdentifier CurrentSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User!;
    }

    private sealed class ServiceContextStore : IChangeFeedStore
    {
        private readonly IChangeFeedStore _inner;
        private readonly List<string> _violations;

        public ServiceContextStore(IChangeFeedStore inner, List<string> violations)
        {
            _inner = inner;
            _violations = violations;
        }

        public IDisposable EnterOwnerScope(CancellationToken cancellationToken = default) =>
            Guarded(nameof(EnterOwnerScope), () => _inner.EnterOwnerScope(cancellationToken));

        public ChangeFeedSubscription? ReadSubscription() =>
            Guarded(nameof(ReadSubscription), _inner.ReadSubscription);

        public void WriteSubscription(ChangeFeedSubscription subscription) =>
            Guarded(nameof(WriteSubscription), () =>
            {
                _inner.WriteSubscription(subscription);
                return true;
            });

        public void DeleteSubscription() =>
            Guarded(nameof(DeleteSubscription), () =>
            {
                _inner.DeleteSubscription();
                return true;
            });

        public ChangeFeedQueueEpoch ReadEpoch() =>
            Guarded(nameof(ReadEpoch), _inner.ReadEpoch);

        public ChangeFeedSecurityStamp ReadSecurityStamp() =>
            Guarded(nameof(ReadSecurityStamp), _inner.ReadSecurityStamp);

        public void NoteSecurityChange() =>
            Guarded(nameof(NoteSecurityChange), () =>
            {
                _inner.NoteSecurityChange();
                return true;
            });

        public ChangeFeedWatcherLease ReadLease() =>
            Guarded(nameof(ReadLease), _inner.ReadLease);

        public ChangeFeedWatcherLease HoldLease(TimeSpan duration) =>
            Guarded(nameof(HoldLease), () => _inner.HoldLease(duration));

        public void ReleaseLease() =>
            Guarded(nameof(ReleaseLease), () =>
            {
                _inner.ReleaseLease();
                return true;
            });

        public ChangeFeedQueueSlice ReadPending(ChangeFeedReadBudget? budget = null) =>
            Guarded(nameof(ReadPending), () => _inner.ReadPending(budget));

        public IReadOnlyList<ChangeFeedQueueEntry> Enqueue(
            string volumeId,
            ulong journalId,
            long fromUsn,
            long toUsn,
            IReadOnlyList<ChangeFeedRootDelivery> roots) =>
            Guarded(
                nameof(Enqueue),
                () => _inner.Enqueue(volumeId, journalId, fromUsn, toUsn, roots));

        public void Acknowledge(long sequence) =>
            Guarded(nameof(Acknowledge), () =>
            {
                _inner.Acknowledge(sequence);
                return true;
            });

        public int DiscardUncommitted(string volumeId, ulong journalId, long committedUsn) =>
            Guarded(
                nameof(DiscardUncommitted),
                () => _inner.DiscardUncommitted(volumeId, journalId, committedUsn));

        private TResult Guarded<TResult>(string member, Func<TResult> work)
        {
            using var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);

            if (impersonated is not null)
            {
                lock (_violations)
                {
                    _violations.Add(member);
                }
            }

            return work();
        }
    }

    private sealed class Harness : IDisposable
    {
        private readonly CancellationTokenSource _cancellation = new();
        private readonly Task _listening;

        public Harness(
            bool listen = true,
            bool faultObserverThrows = false,
            bool guardImpersonation = false,
            bool observeAuthorization = false,
            Func<string, CancellationToken, bool>? handoffDrainer = null,
            TimeSpan? handoffDrainBudget = null)
        {
            Workspace = new TemporaryDirectory();
            TrustedRoot = Path.Combine(Workspace.Path, "Guvenilir");
            PipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");

            var service = new ChangeFeedAdmissionService(
                new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
                ownerSid => guardImpersonation
                    ? new ServiceContextStore(
                        new FileSystemChangeFeedStore(Layout(ownerSid)),
                        ImpersonatedStoreCalls)
                    : new FileSystemChangeFeedStore(Layout(ownerSid)),
                null,
                observeAuthorization
                    ? rootPath => new ChangeFeedPathAuthorizer(rootPath, directory =>
                    {
                        using var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);

                        lock (AuthorizationContexts)
                        {
                            AuthorizationContexts.Add(impersonated?.User);
                        }

                        return Directory.Exists(directory);
                    })
                    : null,
                handoffDrainer: (ownerSid, cancellationToken) =>
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    Interlocked.Increment(ref _handoffDrainCount);
                    using var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);
                    lock (HandoffDrainContexts)
                    {
                        HandoffDrainContexts.Add(impersonated?.User);
                    }

                    return handoffDrainer?.Invoke(ownerSid, cancellationToken) ?? true;
                },
                handoffDrainBudget: handoffDrainBudget);

            Server = new ChangeFeedPipeServer(
                service,
                PipeName,
                additionalServerPrincipal: CurrentSid(),
                onFault: failure =>
                {
                    lock (Faults)
                    {
                        Faults.Add(failure);
                    }

                    if (faultObserverThrows)
                    {
                        throw new InvalidOperationException("Hata gözlemcisi bilerek patlıyor.");
                    }
                });

            Client = new ChangeFeedClient(
                PipeName,
                new HashSet<SecurityIdentifier> { CurrentSid() });

            _listening = listen
                ? Server.ListenAsync(_cancellation.Token)
                : Task.CompletedTask;
        }

        public List<Exception> Faults { get; } = new();

        public List<string> ImpersonatedStoreCalls { get; } = new();

        public List<SecurityIdentifier?> AuthorizationContexts { get; } = new();

        public List<SecurityIdentifier?> HandoffDrainContexts { get; } = new();

        private int _handoffDrainCount;

        public int HandoffDrainCount => Volatile.Read(ref _handoffDrainCount);

        public int FaultCount
        {
            get
            {
                lock (Faults)
                {
                    return Faults.Count;
                }
            }
        }

        public TemporaryDirectory Workspace { get; }

        public string TrustedRoot { get; }

        public string PipeName { get; }

        public ChangeFeedPipeServer Server { get; }

        public ChangeFeedClient Client { get; }

        public Task<ChangeFeedResponse> SendAsync(
            ChangeFeedRequest request,
            CancellationToken cancellationToken = default) =>
            Client.SendAsync(request, cancellationToken);

        public ChangeFeedStoreLayout LayoutForCurrentUser() => Layout(CurrentSid().Value);

        public Exception? ListenerFault =>
            _listening.IsFaulted ? _listening.Exception!.GetBaseException() : null;

        public Task ReadyAsync() => WaitUntilAsync(
            () => Server.AvailableSlots == ChangeFeedProtocol.MaximumConcurrentConnections,
            TimeSpan.FromSeconds(10));

        public string OwnerDirectory() => Layout(CurrentSid().Value).OwnerDirectory;

        public string SubscriptionPath() => Layout(CurrentSid().Value).SubscriptionPath;

        public FileSystemChangeFeedStore OwnerStore() =>
            new(Layout(CurrentSid().Value));

        public IReadOnlyList<ChangeFeedSubscribedRoot> StoredSubscription()
        {
            var store = new FileSystemChangeFeedStore(Layout(CurrentSid().Value));
            return store.ReadSubscription()?.Roots ?? Array.Empty<ChangeFeedSubscribedRoot>();
        }

        public IReadOnlyList<string> StoredRoots()
        {
            var store = new FileSystemChangeFeedStore(Layout(CurrentSid().Value));
            return store.ReadSubscription()?.Roots.Select(root => root.RootPath).ToArray()
                ?? Array.Empty<string>();
        }

        private ChangeFeedStoreLayout Layout(string ownerSid) =>
            ChangeFeedStoreLayout.ForTrustedOwner(TrustedRoot, ownerSid, CurrentSid().Value);

        public void Dispose()
        {
            _cancellation.Cancel();

            try
            {
                _listening.Wait(TimeSpan.FromSeconds(10));
            }
            catch (AggregateException)
            {
            }

            _cancellation.Dispose();
            Workspace.Dispose();
        }
    }
}
