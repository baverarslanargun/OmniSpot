namespace SmartFileLauncher.Core.ChangeFeed;

public sealed class ChangeFeedBatch
{
    private static readonly IReadOnlyList<ChangeFeedEvent> NoEvents = Array.Empty<ChangeFeedEvent>();

    private ChangeFeedBatch(
        ChangeFeedStatus status,
        ChangeFeedGapReason gapReason,
        ChangeFeedFaultReason faultReason,
        string? diagnostics,
        IReadOnlyList<ChangeFeedEvent> events)
    {
        Status = status;
        GapReason = gapReason;
        FaultReason = faultReason;
        Diagnostics = diagnostics;
        Events = events;
    }

    public ChangeFeedStatus Status { get; }

    public ChangeFeedGapReason GapReason { get; }

    public ChangeFeedFaultReason FaultReason { get; }

    public string? Diagnostics { get; }

    public IReadOnlyList<ChangeFeedEvent> Events { get; }

    public bool HasGap => Status == ChangeFeedStatus.Gap;

    public bool IsFaulted => Status == ChangeFeedStatus.Faulted;

    public static ChangeFeedBatch Ok(IReadOnlyList<ChangeFeedEvent> events) =>
        new(
            ChangeFeedStatus.Ok,
            ChangeFeedGapReason.None,
            ChangeFeedFaultReason.None,
            null,
            events);

    public static ChangeFeedBatch Gap(ChangeFeedGapReason reason)
    {
        if (reason == ChangeFeedGapReason.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "Boşluk sonucu için bir neden verilmelidir.");
        }

        return new ChangeFeedBatch(
            ChangeFeedStatus.Gap,
            reason,
            ChangeFeedFaultReason.None,
            null,
            NoEvents);
    }

    public static ChangeFeedBatch Faulted(ChangeFeedFaultReason reason, string diagnostics)
    {
        if (reason == ChangeFeedFaultReason.None)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                "Arıza sonucu için bir neden verilmelidir.");
        }

        if (string.IsNullOrWhiteSpace(diagnostics))
        {
            throw new ArgumentException("Arıza tanısı boş olamaz.", nameof(diagnostics));
        }

        return new ChangeFeedBatch(
            ChangeFeedStatus.Faulted,
            ChangeFeedGapReason.None,
            reason,
            diagnostics,
            NoEvents);
    }
}

public enum ChangeFeedStatus
{
    Ok,
    Gap,
    Faulted
}

public enum ChangeFeedGapReason
{
    None = 0,
    JournalIdChanged,
    CursorOutsideJournal,
    RootIdentityChanged,
    RootUnavailable,
    JournalUnavailable,
    FeedStateInvalid,
    DeliveryQueueOverflow,
    NotYetSynchronized
}

public enum ChangeFeedFaultReason
{
    None = 0,
    NativeProtocolRejected
}
