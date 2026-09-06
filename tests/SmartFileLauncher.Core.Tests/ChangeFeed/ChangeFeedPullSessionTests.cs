using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedPullSessionTests
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private const string OtherOwnerSid = "S-1-5-21-9-9-9-1002";
    private const string Root = @"C:\Kok";
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";

    private static readonly ChangeFeedRootGeneration Generation = ChangeFeedRootGeneration.New();

    [Fact]
    public void AHealthyPull_DeliversEventsWithAReceiptAndDeletesNothing()
    {
        using var world = new World();
        world.Enqueue(2);

        var pull = world.Pull();

        Assert.Equal(ChangeFeedDeliveryStatus.Ok, pull.Status);
        Assert.Equal(2, Assert.Single(pull.Page!.Roots).Events.Count);
        Assert.NotNull(pull.Receipt);
        Assert.Null(pull.Continuation);
        Assert.Single(world.QueueFiles());
    }

    [Fact]
    public void APullWithoutASubscription_DeliversNothing()
    {
        using var world = new World(subscribe: false);

        Assert.Equal(ChangeFeedDeliveryStatus.NoSubscription, world.Pull().Status);
    }

    [Fact]
    public void AnAcknowledgeWithTheReceipt_DrainsTheEntry()
    {
        using var world = new World();
        world.Enqueue(1);

        var receipt = world.Pull().Receipt!;

        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Acknowledge(receipt));
        Assert.Empty(world.QueueFiles());
    }

    [Fact]
    public void ATamperedReceipt_IsRefusedAndDeletesNothing()
    {
        using var world = new World();
        world.Enqueue(1);

        var receipt = world.Pull().Receipt!;
        var tampered = receipt[..^1] + (receipt[^1] == '0' ? '1' : '0');

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Acknowledge(tampered));
        Assert.Single(world.QueueFiles());
    }

    [Fact]
    public void AnotherOwnersReceipt_IsRefusedAndDeletesNothing()
    {
        using var world = new World();
        world.Enqueue(1);

        var receipt = world.Pull().Receipt!;

        Assert.Equal(
            ChangeFeedDeliveryStatus.NoSubscription,
            world.Acknowledge(receipt, ownerSid: OtherOwnerSid));
        Assert.Single(world.QueueFiles());
    }

    [Fact]
    public void AReceiptFromBeforeARepair_IsRefusedAndDeletesNothing()
    {
        using var world = new World();
        world.Enqueue(1);

        var receipt = world.Pull().Receipt!;
        world.Corrupt();
        world.Store.ReadPending();

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Acknowledge(receipt));
        Assert.NotEmpty(world.QueueFiles());
    }

    [Fact]
    public void AReceiptFromBeforeADiscard_IsRefusedAndDeletesNothing()
    {
        using var world = new World();
        world.Enqueue(1);
        world.Enqueue(1, toUsn: 20);

        var receipt = world.Pull().Receipt!;
        Assert.Equal(1, world.Store.DiscardUncommitted(VolumeId, JournalId, 15));

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Acknowledge(receipt));
        Assert.Single(world.QueueFiles());
    }

    [Fact]
    public void AReceiptReplayedAfterASuccessfulAcknowledge_IsRefused()
    {
        using var world = new World();
        world.Enqueue(1);
        world.Enqueue(1);

        var receipt = world.Pull().Receipt!;
        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Acknowledge(receipt));

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Acknowledge(receipt));
    }

    [Fact]
    public void AReceiptFromBeforeARestart_IsRefusedAndDeletesNothing()
    {
        using var world = new World();
        world.Enqueue(1);

        var receipt = world.Pull().Receipt!;
        world.Restart();

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Acknowledge(receipt));
        Assert.Single(world.QueueFiles());
    }

    [Fact]
    public void AReceiptFromBeforeAGenerationChange_IsRefusedAndDeletesNothing()
    {
        using var world = new World();
        world.Enqueue(1);

        var receipt = world.Pull().Receipt!;
        world.Resubscribe(ChangeFeedRootGeneration.New());

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Acknowledge(receipt));
        Assert.Single(world.QueueFiles());
    }

    [Fact]
    public void AnEntryThatDoesNotFit_YieldsAContinuationAndNoReceipt()
    {
        using var world = new World(pageBudget: 40);
        world.Enqueue(4);

        var pull = world.Pull();

        Assert.NotNull(pull.Continuation);
        Assert.Null(pull.Receipt);
        Assert.True(pull.Page!.HasMore);
        Assert.Single(world.QueueFiles());
    }

    [Fact]
    public void AResponseThatCompletesOneEntryAndStopsInTheNext_CarriesOnlyAContinuation()
    {
        using var world = new World(pageBudget: 60);
        world.Enqueue(1);
        world.Enqueue(4, toUsn: 20);

        var pull = world.Pull();

        Assert.True(
            pull.Page!.CompletedThroughSequence > 0,
            "Kurulum ilk girdiyi tamamlamalıydı.");
        Assert.NotNull(pull.Continuation);
        Assert.Null(pull.Receipt);
    }

    [Fact]
    public void AResumedChain_PicksUpWhereItStoppedAndClosesWithAReceipt()
    {
        using var world = new World(pageBudget: 40);
        world.Enqueue(4);

        var delivered = new List<string>();
        string? token = null;
        string? receipt = null;

        for (var round = 0; round < 8 && receipt is null; round++)
        {
            var pull = world.Pull(token);
            Assert.Equal(ChangeFeedDeliveryStatus.Ok, pull.Status);

            foreach (var root in pull.Page!.Roots)
            {
                delivered.AddRange(root.Events.Select(change => change.FullPath));
            }

            token = pull.Continuation;
            receipt = pull.Receipt;
        }

        Assert.NotNull(receipt);
        Assert.Equal(
            new[] { Path(0), Path(1), Path(2), Path(3) },
            delivered);
        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Acknowledge(receipt!));
        Assert.Empty(world.QueueFiles());
    }

    [Fact]
    public void AResumedChain_DoesNotAuthorizeThePrefixAgain()
    {
        using var world = new World(pageBudget: 40);
        world.Enqueue(4);

        var first = world.Pull();
        Assert.NotNull(first.Continuation);

        Assert.Contains(Parent(0), world.AuthorizedPaths);
        world.AuthorizedPaths.Clear();

        world.Pull(first.Continuation);

        Assert.DoesNotContain(Parent(0), world.AuthorizedPaths);
        Assert.NotEmpty(world.AuthorizedPaths);
    }

    [Fact]
    public void AStaleContinuation_IsRefusedAndTheNextPullStartsOver()
    {
        using var world = new World(pageBudget: 40);
        world.Enqueue(4);

        var token = world.Pull().Continuation!;
        world.Corrupt();
        world.Store.ReadPending();

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Pull(token).Status);
        Assert.NotEmpty(world.QueueFiles());
        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Pull().Status);
    }

    [Fact]
    public void AnAppendDuringAnOpenChain_NeitherBreaksItNorJoinsIt()
    {
        using var world = new World(pageBudget: 60);
        world.Enqueue(4);

        var token = world.Pull().Continuation!;
        world.Enqueue(1, toUsn: 20);

        var receipt = world.Drive(token);

        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Acknowledge(receipt));
        Assert.Single(world.QueueFiles());
        Assert.True(
            world.Pull().Page!.HasMore == false,
            "Eklenen girdi tek başına kaldığı için devamı bildirilmemeliydi.");
    }

    [Fact]
    public void AReceiptFromBeforeAnOverflow_IsRefusedAndDeletesNothing()
    {
        using var world = new World(maximumEntryCount: 1);
        world.Enqueue(1);

        var receipt = world.Pull().Receipt!;
        world.Enqueue(1, toUsn: 20);

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Acknowledge(receipt));
        Assert.Single(world.QueueFiles());
    }

    [Fact]
    public void AnEmptyQueue_DeliversNothingAndIssuesNoToken()
    {
        using var world = new World();

        var pull = world.Pull();

        Assert.Equal(ChangeFeedDeliveryStatus.Ok, pull.Status);
        Assert.Empty(pull.Page!.Roots);
        Assert.Null(pull.Continuation);
        Assert.Null(pull.Receipt);
    }

    [Fact]
    public void APullThatRepairsTheQueue_StillIssuesAUsableReceipt()
    {
        using var world = new World();
        world.Enqueue(1);
        world.Corrupt();

        var pull = world.Pull();

        Assert.Equal(ChangeFeedDeliveryStatus.Ok, pull.Status);
        Assert.NotNull(pull.Receipt);
        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Acknowledge(pull.Receipt!));
        Assert.Empty(world.QueueFiles());
    }

    [Fact]
    public void AResumeThatHitsARepair_IsRefusedWithZeroDeletion()
    {
        using var world = new World(pageBudget: 40);
        world.Enqueue(4);

        var token = world.Pull().Continuation!;
        world.Corrupt();

        var resumed = world.Pull(token);

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, resumed.Status);
        Assert.NotEmpty(world.QueueFiles());
    }

    [Fact]
    public void AnOldContinuation_DoesNotRetireTheCurrentChain()
    {
        using var world = new World(pageBudget: 40);
        world.Enqueue(4);

        var first = world.Pull().Continuation!;
        var second = world.Pull(first).Continuation!;

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Pull(first).Status);
        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Pull(second).Status);
    }

    [Fact]
    public void ACancelledPull_LeavesTheOpenChainAlone()
    {
        using var world = new World(pageBudget: 40);
        world.Enqueue(4);

        var token = world.Pull().Continuation!;

        using var cancellation = new CancellationTokenSource();
        world.CancelWhileAuthorizing(cancellation);

        Assert.ThrowsAny<OperationCanceledException>(
            () => world.Pull(token, cancellation.Token));

        world.CancelWhileAuthorizing(null);
        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Pull(token).Status);
    }

    [Fact]
    public void ATokenlessPull_RetiresTheOpenChain()
    {
        using var world = new World(pageBudget: 40);
        world.Enqueue(4);

        var token = world.Pull().Continuation!;

        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Pull().Status);
        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Pull(token).Status);
        Assert.NotEmpty(world.QueueFiles());
    }

    [Fact]
    public void TheFinalReceipt_AcknowledgesEveryEntryTheChainCovered()
    {
        using var world = new World(pageBudget: 60);
        world.Enqueue(1);
        world.Enqueue(4, toUsn: 20);

        Assert.Equal(2, world.QueueFiles().Length);

        var receipt = world.Drive(null);

        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Acknowledge(receipt));
        Assert.Empty(world.QueueFiles());
    }

    [Fact]
    public void ASecurityChange_RejectsTheOpenChainAndDeletesNothing()
    {
        using var world = new World(pageBudget: 40);
        world.Enqueue(4);

        var token = world.Pull().Continuation!;
        world.Store.NoteSecurityChange();

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Pull(token).Status);
        Assert.Single(world.QueueFiles());

        Assert.Equal(ChangeFeedDeliveryStatus.Ok, world.Pull().Status);
    }

    [Fact]
    public void ASecurityChange_RejectsAReceiptTakenBeforeIt()
    {
        using var world = new World();
        world.Enqueue(1);

        var receipt = world.Pull().Receipt!;
        world.Store.NoteSecurityChange();

        Assert.Equal(ChangeFeedDeliveryStatus.StaleChain, world.Acknowledge(receipt));
        Assert.Single(world.QueueFiles());
    }

    private static string Path(int index) => @"C:\Kok\alt" + index + @"\dosya.txt";

    private static string Parent(int index) => @"C:\Kok\alt" + index;

    private sealed class World : IDisposable
    {
        private readonly TemporaryDirectory _directory = new();
        private readonly long _pageBudget;
        private ChangeFeedDeliveryLedger _ledger = new();
        private CancellationTokenSource? _cancelDuringAuthorization;

        public World(
            bool subscribe = true,
            long pageBudget = 4096,
            int maximumEntryCount = FileSystemChangeFeedStore.DefaultMaximumEntryCount)
        {
            _pageBudget = pageBudget;
            Layout = ChangeFeedStoreLayout.ForOwner(_directory.Path, OwnerSid);
            Store = new FileSystemChangeFeedStore(Layout, maximumEntryCount: maximumEntryCount);

            if (subscribe)
            {
                Resubscribe(Generation);
            }
        }

        public ChangeFeedStoreLayout Layout { get; }

        public FileSystemChangeFeedStore Store { get; }

        public List<string> AuthorizedPaths { get; } = new();

        public void Resubscribe(ChangeFeedRootGeneration generation) =>
            Store.WriteSubscription(new ChangeFeedSubscription(
                OwnerSid,
                new[]
                {
                    new ChangeFeedSubscribedRoot(
                        Root,
                        new ChangeFeedRootIdentity("vol-1", "node-1"),
                        generation)
                }));

        public void Restart() => _ledger = new ChangeFeedDeliveryLedger();

        public void Enqueue(int events, long toUsn = 10) => Store.Enqueue(
            VolumeId,
            JournalId,
            0,
            toUsn,
            new[]
            {
                new ChangeFeedRootDelivery(
                    Root,
                    ChangeFeedBatch.Ok(Enumerable
                        .Range(0, events)
                        .Select(index => new ChangeFeedEvent(
                            ChangeFeedEventKind.Created,
                            Path(index),
                            false))
                        .ToArray()),
                    Generation)
            });

        public void Corrupt() => File.WriteAllText(
            System.IO.Path.Combine(Layout.QueueDirectory, "0000000000000000900.json"),
            "{ bozuk");

        public string[] QueueFiles() =>
            Directory.GetFiles(Layout.QueueDirectory, "*.json");

        public ChangeFeedPullResult Pull(
            string? continuation = null,
            CancellationToken cancellationToken = default) =>
            Session().Pull(OwnerSid, continuation, cancellationToken);

        public void CancelWhileAuthorizing(CancellationTokenSource? source) =>
            _cancelDuringAuthorization = source;

        public string Drive(string? continuation)
        {
            for (var round = 0; round < 16; round++)
            {
                var pull = Pull(continuation);

                if (pull.Receipt is { } receipt)
                {
                    return receipt;
                }

                continuation = pull.Continuation
                    ?? throw new InvalidOperationException("Zincir belgesiz kapandı.");
            }

            throw new InvalidOperationException("Zincir kapanmadı.");
        }

        public ChangeFeedDeliveryStatus Acknowledge(string receipt, string? ownerSid = null) =>
            Session().Acknowledge(ownerSid ?? OwnerSid, receipt);

        public void Dispose() => _directory.Dispose();

        private ChangeFeedPullSession Session() =>
            new(
                Store,
                _ledger,
                (subscription, slice, start, cancellationToken) => new ChangeFeedDeliveryProjector(
                    rootPath => new ChangeFeedPathAuthorizer(rootPath, directory =>
                    {
                        AuthorizedPaths.Add(directory);
                        _cancelDuringAuthorization?.Cancel();
                        return true;
                    }),
                    new Measure(),
                    _pageBudget).Walk(subscription, slice, start, cancellationToken));
    }

    private sealed class Measure : IChangeFeedPageMeasure
    {
        public long Envelope => 2;

        public long Root(string rootPath) => rootPath.Length;

        public long Event(ChangeFeedEvent change) =>
            change.FullPath.Length + (change.OldPath?.Length ?? 0);
    }
}
