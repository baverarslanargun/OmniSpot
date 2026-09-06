using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedDeliveryLedgerTests
{
    private const string OwnerSid = "S-1-5-21-1-2-3-1001";
    private const string OtherOwnerSid = "S-1-5-21-1-2-3-1002";
    private const string FirstRoot = @"C:\Kok";
    private const string SecondRoot = @"C:\Diger";

    private static readonly ChangeFeedQueueEpoch Epoch = ChangeFeedQueueEpoch.New();
    private static readonly ChangeFeedSecurityStamp Security = ChangeFeedSecurityStamp.New();
    private static readonly ChangeFeedRootGeneration Generation = ChangeFeedRootGeneration.New();
    private static readonly ChangeFeedDeliveryPosition Position = new(7, 1, 12);

    [Fact]
    public void AFreshContinuation_OpensWithTheSameBinding()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var binding = Binding();

        var token = ledger.IssueContinuation(binding, Position, snapshotThroughSequence: 9);
        var opened = ledger.OpenContinuation(token, Binding());

        Assert.NotNull(opened);
        Assert.Equal(Position, opened!.Position);
        Assert.Equal(9, opened.SnapshotThroughSequence);
    }

    [Fact]
    public void ATamperedToken_IsRejected()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        var tampered = token[..^1] + (token[^1] == '0' ? '1' : '0');

        Assert.Null(ledger.OpenContinuation(tampered, Binding()));
        Assert.Null(ledger.OpenContinuation(string.Empty, Binding()));
        Assert.Null(ledger.OpenContinuation(null, Binding()));
    }

    [Fact]
    public void AnotherOwnerCannotOpenTheChain()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        Assert.Null(ledger.OpenContinuation(token, Binding(ownerSid: OtherOwnerSid)));
    }

    [Fact]
    public void AnEpochChange_RejectsTheChain()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        Assert.Null(ledger.OpenContinuation(token, Binding(epoch: ChangeFeedQueueEpoch.New())));
    }

    [Fact]
    public void AGenerationChange_RejectsTheChain()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        var renewed = new[]
        {
            Root(FirstRoot, "node-1", ChangeFeedRootGeneration.New()),
            Root(SecondRoot, "node-2", Generation)
        };

        Assert.Null(ledger.OpenContinuation(token, Binding(roots: renewed)));
    }

    [Fact]
    public void ARootAddedOutsideTheDelivery_RejectsTheChain()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        var grown = new[]
        {
            Root(FirstRoot, "node-1", Generation),
            Root(SecondRoot, "node-2", Generation),
            Root(@"C:\Ucuncu", "node-3", Generation)
        };

        Assert.Null(ledger.OpenContinuation(token, Binding(roots: grown)));
    }

    [Fact]
    public void ARootIdentityChange_RejectsTheChain()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        var moved = new[]
        {
            Root(FirstRoot, "node-9", Generation),
            Root(SecondRoot, "node-2", Generation)
        };

        Assert.Null(ledger.OpenContinuation(token, Binding(roots: moved)));
    }

    [Fact]
    public void ARootRemoved_RejectsTheChain()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        var shrunk = new[] { Root(FirstRoot, "node-1", Generation) };

        Assert.Null(ledger.OpenContinuation(token, Binding(roots: shrunk)));
    }

    [Fact]
    public void AProtocolVersionChange_RejectsTheChain()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        Assert.Null(ledger.OpenContinuation(token, Binding(protocolVersion: 2)));
    }

    [Fact]
    public void RootOrder_DoesNotChangeTheBinding()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        var reordered = new[]
        {
            Root(SecondRoot, "node-2", Generation),
            Root(FirstRoot, "node-1", Generation)
        };

        Assert.NotNull(ledger.OpenContinuation(token, Binding(roots: reordered)));
    }

    [Fact]
    public void AReceipt_IsNotAContinuationAndTheOtherWayAround()
    {
        var ledger = new ChangeFeedDeliveryLedger();

        var receipt = ledger.IssueReceipt(Binding(), completedThroughSequence: 41);
        Assert.Null(ledger.OpenContinuation(receipt, Binding()));
        Assert.Equal(41, ledger.OpenReceipt(receipt, Binding())!.CompletedThroughSequence);

        var continuation = ledger.IssueContinuation(Binding(), Position, 9);
        Assert.Null(ledger.OpenReceipt(continuation, Binding()));
    }

    [Fact]
    public void ANewChain_RetiresTheOwnersPreviousOne()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var first = ledger.IssueContinuation(Binding(), Position, 9);
        var second = ledger.IssueContinuation(Binding(), Position, 9);

        Assert.Null(ledger.OpenContinuation(first, Binding()));
        Assert.NotNull(ledger.OpenContinuation(second, Binding()));
    }

    [Fact]
    public void AForgottenChain_IsRejected()
    {
        var ledger = new ChangeFeedDeliveryLedger();
        var token = ledger.IssueContinuation(Binding(), Position, 9);

        ledger.Forget(OwnerSid);

        Assert.Null(ledger.OpenContinuation(token, Binding()));
    }

    [Fact]
    public void AnExpiredChain_IsRejected()
    {
        var now = 0L;
        var ledger = new ChangeFeedDeliveryLedger(
            () => now,
            lifetime: TimeSpan.FromMilliseconds(100));

        var token = ledger.IssueContinuation(Binding(), Position, 9);

        now = 99;
        Assert.NotNull(ledger.OpenContinuation(token, Binding()));

        now = 100;
        Assert.Null(ledger.OpenContinuation(token, Binding()));
    }

    [Fact]
    public void TheLedger_StaysWithinItsOwnerCeiling()
    {
        var now = 0L;
        var ledger = new ChangeFeedDeliveryLedger(() => now, maximumOwners: 2);

        var first = ledger.IssueContinuation(Binding(), Position, 9);
        now = 1;
        var second = ledger.IssueContinuation(Binding(ownerSid: OtherOwnerSid), Position, 9);
        now = 2;
        var third = ledger.IssueContinuation(Binding(ownerSid: "S-1-5-21-1-2-3-1003"), Position, 9);

        Assert.Null(ledger.OpenContinuation(first, Binding()));
        Assert.NotNull(ledger.OpenContinuation(second, Binding(ownerSid: OtherOwnerSid)));
        Assert.NotNull(ledger.OpenContinuation(third, Binding(ownerSid: "S-1-5-21-1-2-3-1003")));
    }

    [Fact]
    public void AnUnknownEpoch_CannotBeBound()
    {
        Assert.Throws<ArgumentException>(
            () => Binding(epoch: ChangeFeedQueueEpoch.Unknown));
    }

    private static ChangeFeedChainBinding Binding(
        string ownerSid = OwnerSid,
        int protocolVersion = 1,
        ChangeFeedQueueEpoch? epoch = null,
        ChangeFeedSecurityStamp? security = null,
        IReadOnlyList<ChangeFeedSubscribedRoot>? roots = null) =>
        new(
            ownerSid,
            protocolVersion,
            epoch ?? Epoch,
            security ?? Security,
            roots ?? new[]
            {
                Root(FirstRoot, "node-1", Generation),
                Root(SecondRoot, "node-2", Generation)
            });

    private static ChangeFeedSubscribedRoot Root(
        string path,
        string nodeId,
        ChangeFeedRootGeneration generation) =>
        new(path, new ChangeFeedRootIdentity("vol-1", nodeId), generation);
}
