namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed record ChangeFeedEventDto(
    ChangeFeedEventKind Kind,
    string Path,
    bool IsDirectory,
    string? OldPath);

public sealed record ChangeFeedRootPageDto(
    string RootPath,
    IReadOnlyList<ChangeFeedEventDto> Events,
    ChangeFeedGapReason ProducerGap,
    ChangeFeedFaultReason ProducerFault,
    bool AuthorizationGap,
    bool PayloadTooLarge);

public sealed record ChangeFeedDeliveryDto(
    IReadOnlyList<ChangeFeedRootPageDto> Roots,
    bool HasMore,
    string? Continuation,
    string? Receipt);

public static class ChangeFeedDeliveryContract
{
    public static ChangeFeedDeliveryDto ToWire(
        ChangeFeedDeliveryPage page,
        string? continuation,
        string? receipt)
    {
        ArgumentNullException.ThrowIfNull(page);

        return new ChangeFeedDeliveryDto(
            page.Roots.Select(ToWire).ToArray(),
            page.HasMore,
            continuation,
            receipt);
    }

    public static ChangeFeedRootPageDto ToWire(ChangeFeedRootPage root)
    {
        ArgumentNullException.ThrowIfNull(root);

        return new ChangeFeedRootPageDto(
            root.RootPath,
            root.Events.Select(ToWire).ToArray(),
            root.ProducerGap,
            root.ProducerFault,
            root.AuthorizationGap,
            root.PayloadTooLarge);
    }

    public static ChangeFeedEventDto ToWire(ChangeFeedEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return new ChangeFeedEventDto(
            change.Kind,
            change.FullPath,
            change.IsDirectory,
            change.OldPath);
    }
}
