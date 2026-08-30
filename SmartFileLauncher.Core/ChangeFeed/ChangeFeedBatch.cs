namespace SmartFileLauncher.Core.ChangeFeed;

/// <summary>
/// Result of a single <see cref="IChangeFeed.Read"/>. The batch stays replayable
/// until <see cref="IChangeFeed.Accept"/> is called.
/// </summary>
public sealed class ChangeFeedBatch
{
    private static readonly IReadOnlyList<ChangeFeedEvent> NoEvents = Array.Empty<ChangeFeedEvent>();

    private ChangeFeedBatch(
        ChangeFeedStatus status,
        ChangeFeedGapReason gapReason,
        IReadOnlyList<ChangeFeedEvent> events)
    {
        Status = status;
        GapReason = gapReason;
        Events = events;
    }

    public ChangeFeedStatus Status { get; }

    public ChangeFeedGapReason GapReason { get; }

    /// <summary>Always empty when <see cref="Status"/> is <see cref="ChangeFeedStatus.Gap"/>.</summary>
    public IReadOnlyList<ChangeFeedEvent> Events { get; }

    public bool HasGap => Status == ChangeFeedStatus.Gap;

    public static ChangeFeedBatch Ok(IReadOnlyList<ChangeFeedEvent> events) =>
        new(ChangeFeedStatus.Ok, ChangeFeedGapReason.None, events);

    public static ChangeFeedBatch Gap(ChangeFeedGapReason reason)
    {
        if (reason == ChangeFeedGapReason.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "Boşluk sonucu için bir neden verilmelidir.");
        }

        return new ChangeFeedBatch(ChangeFeedStatus.Gap, reason, NoEvents);
    }
}

public enum ChangeFeedStatus
{
    Ok,
    Gap
}

/// <summary>
/// Why a feed could not deliver a continuous change stream. Every value forces
/// the consumer to reconcile the affected root, never other roots.
/// </summary>
public enum ChangeFeedGapReason
{
    None = 0,

    /// <summary>The change log was recreated; nothing before it can be trusted.</summary>
    JournalIdChanged,

    /// <summary>The stored cursor no longer falls inside the retained log range.</summary>
    CursorOutsideJournal,

    /// <summary>The root path now resolves to a different volume or directory.</summary>
    RootIdentityChanged,

    /// <summary>The root path could not be opened.</summary>
    RootUnavailable,

    /// <summary>The change log is disabled, being deleted, or otherwise unreadable.</summary>
    JournalUnavailable,

    /// <summary>Persisted feed state is missing, malformed, or in an unsupported format.</summary>
    FeedStateInvalid
}
