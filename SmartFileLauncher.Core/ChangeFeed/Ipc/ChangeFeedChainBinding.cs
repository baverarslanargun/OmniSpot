using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public enum ChangeFeedTokenKind
{
    Continuation,
    Receipt
}

public sealed record ChangeFeedDeliveryPosition(
    long Sequence,
    int DeliveryIndex,
    int EventIndex)
{
    public static readonly ChangeFeedDeliveryPosition Start = new(0, 0, 0);
}

public sealed record ChangeFeedContinuation(
    ChangeFeedDeliveryPosition Position,
    long SnapshotThroughSequence);

public sealed record ChangeFeedReceipt(long CompletedThroughSequence);

public sealed class ChangeFeedChainBinding
{
    private readonly ChangeFeedSubscribedRoot[] _roots;

    public ChangeFeedChainBinding(
        string ownerSid,
        int protocolVersion,
        ChangeFeedQueueEpoch epoch,
        ChangeFeedSecurityStamp security,
        IReadOnlyList<ChangeFeedSubscribedRoot> roots)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);
        ArgumentNullException.ThrowIfNull(roots);

        if (epoch.IsUnknown)
        {
            throw new ArgumentException("Zincir bilinmeyen epoch'a bağlanamaz.", nameof(epoch));
        }

        if (security.IsUnknown)
        {
            throw new ArgumentException(
                "Zincir bilinmeyen güvenlik damgasına bağlanamaz.",
                nameof(security));
        }

        OwnerSid = ownerSid;
        ProtocolVersion = protocolVersion;
        Epoch = epoch;
        Security = security;
        _roots = roots
            .OrderBy(root => root.RootPath, StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    public string OwnerSid { get; }

    public int ProtocolVersion { get; }

    public ChangeFeedQueueEpoch Epoch { get; }

    public ChangeFeedSecurityStamp Security { get; }

    public IReadOnlyList<ChangeFeedSubscribedRoot> Roots => _roots;

    public bool Matches(ChangeFeedChainBinding other)
    {
        ArgumentNullException.ThrowIfNull(other);

        if (!string.Equals(OwnerSid, other.OwnerSid, StringComparison.Ordinal) ||
            ProtocolVersion != other.ProtocolVersion ||
            !Epoch.Matches(other.Epoch) ||
            !Security.Matches(other.Security) ||
            _roots.Length != other._roots.Length)
        {
            return false;
        }

        for (var index = 0; index < _roots.Length; index++)
        {
            if (!SameRoot(_roots[index], other._roots[index]))
            {
                return false;
            }
        }

        return true;
    }

    private static bool SameRoot(ChangeFeedSubscribedRoot left, ChangeFeedSubscribedRoot right) =>
        string.Equals(left.RootPath, right.RootPath, StringComparison.OrdinalIgnoreCase) &&
        left.Identity == right.Identity &&
        left.Generation.Matches(right.Generation);
}
