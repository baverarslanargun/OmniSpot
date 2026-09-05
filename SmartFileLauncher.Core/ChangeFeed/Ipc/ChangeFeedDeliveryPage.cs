namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed class ChangeFeedRootPage
{
    public ChangeFeedRootPage(
        string rootPath,
        IReadOnlyList<ChangeFeedEvent> events,
        ChangeFeedGapReason producerGap,
        ChangeFeedFaultReason producerFault,
        bool authorizationGap,
        bool payloadTooLarge)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        RootPath = rootPath;
        Events = events ?? throw new ArgumentNullException(nameof(events));
        ProducerGap = producerGap;
        ProducerFault = producerFault;
        AuthorizationGap = authorizationGap;
        PayloadTooLarge = payloadTooLarge;
    }

    public string RootPath { get; }

    public IReadOnlyList<ChangeFeedEvent> Events { get; }

    public ChangeFeedGapReason ProducerGap { get; }

    public ChangeFeedFaultReason ProducerFault { get; }

    public bool AuthorizationGap { get; }

    public bool PayloadTooLarge { get; }

    public bool HasAnyGap =>
        ProducerGap != ChangeFeedGapReason.None ||
        ProducerFault != ChangeFeedFaultReason.None ||
        AuthorizationGap ||
        PayloadTooLarge;
}

public sealed class ChangeFeedDeliveryPage
{
    public ChangeFeedDeliveryPage(
        IReadOnlyList<ChangeFeedRootPage> roots,
        long completedThroughSequence,
        bool hasMore)
    {
        Roots = roots ?? throw new ArgumentNullException(nameof(roots));
        CompletedThroughSequence = completedThroughSequence;
        HasMore = hasMore;
    }

    public IReadOnlyList<ChangeFeedRootPage> Roots { get; }

    public long CompletedThroughSequence { get; }

    public bool HasMore { get; }
}
