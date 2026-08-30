namespace SmartFileLauncher.Core.ChangeFeed;

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

public enum ChangeFeedGapReason
{
    None = 0,
    JournalIdChanged,
    CursorOutsideJournal,
    RootIdentityChanged,
    RootUnavailable,
    JournalUnavailable,
    FeedStateInvalid
}
