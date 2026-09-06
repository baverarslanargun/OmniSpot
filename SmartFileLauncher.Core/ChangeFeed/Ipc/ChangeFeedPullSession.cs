using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public delegate ChangeFeedDeliveryWalk ChangeFeedDeliveryWalker(
    ChangeFeedSubscription subscription,
    ChangeFeedQueueSlice slice,
    ChangeFeedDeliveryPosition start,
    CancellationToken cancellationToken);

public enum ChangeFeedDeliveryStatus
{
    Ok,
    NoSubscription,
    StaleChain
}

public sealed class ChangeFeedPullResult
{
    private ChangeFeedPullResult(
        ChangeFeedDeliveryStatus status,
        ChangeFeedDeliveryPage? page,
        string? continuation,
        string? receipt)
    {
        Status = status;
        Page = page;
        Continuation = continuation;
        Receipt = receipt;
    }

    public ChangeFeedDeliveryStatus Status { get; }

    public ChangeFeedDeliveryPage? Page { get; }

    public string? Continuation { get; }

    public string? Receipt { get; }

    public static ChangeFeedPullResult Delivered(
        ChangeFeedDeliveryPage page,
        string? continuation,
        string? receipt) =>
        new(ChangeFeedDeliveryStatus.Ok, page, continuation, receipt);

    public static ChangeFeedPullResult Refused(ChangeFeedDeliveryStatus status) =>
        new(status, null, null, null);
}

public sealed class ChangeFeedPullSession
{
    private readonly IChangeFeedStore _store;
    private readonly ChangeFeedDeliveryLedger _ledger;
    private readonly ChangeFeedDeliveryWalker _walker;
    private readonly int _protocolVersion;

    public ChangeFeedPullSession(
        IChangeFeedStore store,
        ChangeFeedDeliveryLedger ledger,
        ChangeFeedDeliveryWalker walker,
        int protocolVersion = ChangeFeedProtocol.Version)
    {
        ArgumentNullException.ThrowIfNull(store);
        ArgumentNullException.ThrowIfNull(ledger);
        ArgumentNullException.ThrowIfNull(walker);

        _store = store;
        _ledger = ledger;
        _walker = walker;
        _protocolVersion = protocolVersion;
    }

    public ChangeFeedPullResult Pull(
        string ownerSid,
        string? continuation,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);

        using var scope = _store.EnterOwnerScope(cancellationToken);

        if (Bind(ownerSid) is not { } opening)
        {
            return ChangeFeedPullResult.Refused(ChangeFeedDeliveryStatus.NoSubscription);
        }

        var subscription = _store.ReadSubscription()!;
        var resuming = !string.IsNullOrEmpty(continuation);
        var start = ChangeFeedDeliveryPosition.Start;
        long? snapshotThrough = null;

        if (resuming)
        {
            if (_ledger.OpenContinuation(continuation, opening) is not { } resumed)
            {
                return ChangeFeedPullResult.Refused(ChangeFeedDeliveryStatus.StaleChain);
            }

            start = resumed.Position;
            snapshotThrough = resumed.SnapshotThroughSequence;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var slice = _store.ReadPending();

        if (Bind(ownerSid) is not { } binding)
        {
            return ChangeFeedPullResult.Refused(ChangeFeedDeliveryStatus.NoSubscription);
        }

        if (!binding.Matches(opening))
        {
            if (resuming)
            {
                return ChangeFeedPullResult.Refused(ChangeFeedDeliveryStatus.StaleChain);
            }

            subscription = _store.ReadSubscription()!;
        }

        cancellationToken.ThrowIfCancellationRequested();

        var upperBound = snapshotThrough ?? LastSequence(slice);

        if (snapshotThrough is not null)
        {
            slice = Limit(slice, upperBound);
        }

        var walk = _walker(subscription, slice, start, cancellationToken);

        cancellationToken.ThrowIfCancellationRequested();

        if (walk.NextPosition is { } next)
        {
            return ChangeFeedPullResult.Delivered(
                walk.Page,
                _ledger.IssueContinuation(binding, next, upperBound),
                receipt: null);
        }

        if (walk.Page.CompletedThroughSequence > 0)
        {
            return ChangeFeedPullResult.Delivered(
                walk.Page,
                continuation: null,
                _ledger.IssueReceipt(binding, walk.Page.CompletedThroughSequence));
        }

        if (!resuming)
        {
            _ledger.Forget(ownerSid);
        }

        return ChangeFeedPullResult.Delivered(walk.Page, continuation: null, receipt: null);
    }

    public ChangeFeedDeliveryStatus Acknowledge(
        string ownerSid,
        string? receipt,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);

        using var scope = _store.EnterOwnerScope(cancellationToken);

        if (Bind(ownerSid) is not { } binding)
        {
            return ChangeFeedDeliveryStatus.NoSubscription;
        }

        if (_ledger.OpenReceipt(receipt, binding) is not { } opened)
        {
            return ChangeFeedDeliveryStatus.StaleChain;
        }

        cancellationToken.ThrowIfCancellationRequested();

        _store.Acknowledge(opened.CompletedThroughSequence);
        _ledger.Forget(ownerSid);

        return ChangeFeedDeliveryStatus.Ok;
    }

    private ChangeFeedChainBinding? Bind(string ownerSid)
    {
        var subscription = _store.ReadSubscription();

        if (subscription is null ||
            !string.Equals(subscription.OwnerSid, ownerSid, StringComparison.Ordinal))
        {
            return null;
        }

        return new ChangeFeedChainBinding(
            ownerSid,
            _protocolVersion,
            _store.ReadEpoch(),
            _store.ReadSecurityStamp(),
            subscription.Roots);
    }

    private static ChangeFeedQueueSlice Limit(ChangeFeedQueueSlice slice, long throughSequence)
    {
        var kept = slice.Entries
            .Where(entry => entry.Sequence <= throughSequence)
            .ToArray();

        return kept.Length == slice.Entries.Count
            ? slice
            : new ChangeFeedQueueSlice(kept, true);
    }

    private static long LastSequence(ChangeFeedQueueSlice slice) =>
        slice.Entries.Count == 0 ? 0 : slice.Entries[^1].Sequence;
}
