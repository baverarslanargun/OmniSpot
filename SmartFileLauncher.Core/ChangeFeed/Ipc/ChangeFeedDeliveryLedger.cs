using System.Security.Cryptography;
using System.Text;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed class ChangeFeedDeliveryLedger
{
    public const int DefaultMaximumOwners = 64;

    public const int TokenLength = 64;

    private readonly object _gate = new();
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);
    private readonly Func<long> _monotonicMilliseconds;
    private readonly long _lifetimeMilliseconds;
    private readonly int _maximumOwners;

    public ChangeFeedDeliveryLedger(
        Func<long>? monotonicMilliseconds = null,
        TimeSpan? lifetime = null,
        int maximumOwners = DefaultMaximumOwners)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumOwners);

        var span = lifetime ?? DefaultLifetime;
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(span.Ticks);

        _monotonicMilliseconds = monotonicMilliseconds ?? (() => Environment.TickCount64);
        _lifetimeMilliseconds = (long)span.TotalMilliseconds;
        _maximumOwners = maximumOwners;
    }

    public static TimeSpan DefaultLifetime => TimeSpan.FromMinutes(5);

    public string IssueContinuation(
        ChangeFeedChainBinding binding,
        ChangeFeedDeliveryPosition position,
        long snapshotThroughSequence)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentNullException.ThrowIfNull(position);
        ArgumentOutOfRangeException.ThrowIfNegative(snapshotThroughSequence);

        return Issue(
            binding,
            ChangeFeedTokenKind.Continuation,
            new ChangeFeedContinuation(position, snapshotThroughSequence),
            completedThroughSequence: 0);
    }

    public string IssueReceipt(ChangeFeedChainBinding binding, long completedThroughSequence)
    {
        ArgumentNullException.ThrowIfNull(binding);
        ArgumentOutOfRangeException.ThrowIfNegative(completedThroughSequence);

        return Issue(
            binding,
            ChangeFeedTokenKind.Receipt,
            continuation: null,
            completedThroughSequence);
    }

    public ChangeFeedContinuation? OpenContinuation(string? token, ChangeFeedChainBinding current) =>
        Open(token, ChangeFeedTokenKind.Continuation, current)?.Continuation;

    public ChangeFeedReceipt? OpenReceipt(string? token, ChangeFeedChainBinding current) =>
        Open(token, ChangeFeedTokenKind.Receipt, current) is { } entry
            ? new ChangeFeedReceipt(entry.CompletedThroughSequence)
            : null;

    public void Forget(string ownerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);

        lock (_gate)
        {
            _entries.Remove(ownerSid);
        }
    }

    private string Issue(
        ChangeFeedChainBinding binding,
        ChangeFeedTokenKind kind,
        ChangeFeedContinuation? continuation,
        long completedThroughSequence)
    {
        var token = RandomNumberGenerator.GetHexString(TokenLength, lowercase: true);
        var now = _monotonicMilliseconds();

        lock (_gate)
        {
            DropExpired(now);

            if (!_entries.ContainsKey(binding.OwnerSid) && _entries.Count >= _maximumOwners)
            {
                DropOldest();
            }

            _entries[binding.OwnerSid] = new Entry(
                Encoding.UTF8.GetBytes(token),
                kind,
                binding,
                continuation,
                completedThroughSequence,
                now);
        }

        return token;
    }

    private Entry? Open(string? token, ChangeFeedTokenKind kind, ChangeFeedChainBinding current)
    {
        ArgumentNullException.ThrowIfNull(current);

        if (string.IsNullOrEmpty(token))
        {
            return null;
        }

        var now = _monotonicMilliseconds();
        var offered = Encoding.UTF8.GetBytes(token);

        lock (_gate)
        {
            DropExpired(now);

            if (!_entries.TryGetValue(current.OwnerSid, out var entry) ||
                entry.Kind != kind ||
                !CryptographicOperations.FixedTimeEquals(entry.Token, offered) ||
                !entry.Binding.Matches(current))
            {
                return null;
            }

            return entry;
        }
    }

    private void DropExpired(long now)
    {
        var stale = _entries
            .Where(pair => now - pair.Value.IssuedAt >= _lifetimeMilliseconds)
            .Select(pair => pair.Key)
            .ToArray();

        foreach (var owner in stale)
        {
            _entries.Remove(owner);
        }
    }

    private void DropOldest()
    {
        var oldest = _entries
            .OrderBy(pair => pair.Value.IssuedAt)
            .Select(pair => pair.Key)
            .FirstOrDefault();

        if (oldest is not null)
        {
            _entries.Remove(oldest);
        }
    }

    private sealed record Entry(
        byte[] Token,
        ChangeFeedTokenKind Kind,
        ChangeFeedChainBinding Binding,
        ChangeFeedContinuation? Continuation,
        long CompletedThroughSequence,
        long IssuedAt);
}
