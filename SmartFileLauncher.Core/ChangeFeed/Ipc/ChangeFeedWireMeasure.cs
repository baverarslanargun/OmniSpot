namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed class ChangeFeedWireMeasure : IChangeFeedPageMeasure
{
    private const int SeparatorBytes = 1;

    private static readonly string TokenPlaceholder =
        new('0', ChangeFeedDeliveryLedger.TokenLength);

    private static readonly ChangeFeedGapReason WidestGap =
        Enum.GetValues<ChangeFeedGapReason>()
            .OrderByDescending(reason => reason.ToString().Length)
            .First();

    private static readonly ChangeFeedFaultReason WidestFault =
        Enum.GetValues<ChangeFeedFaultReason>()
            .OrderByDescending(reason => reason.ToString().Length)
            .First();

    public ChangeFeedWireMeasure()
    {
        Envelope = ChangeFeedMessageChannel.MeasureResponse(
            new ChangeFeedResponse(
                ChangeFeedProtocol.Version,
                ChangeFeedResponseStatus.Ok,
                null,
                null,
                new ChangeFeedDeliveryDto(
                    Array.Empty<ChangeFeedRootPageDto>(),
                    true,
                    TokenPlaceholder,
                    TokenPlaceholder)));
    }

    public long Envelope { get; }

    public long Root(string rootPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);

        return ChangeFeedMessageChannel.MeasureResponse(
            new ChangeFeedRootPageDto(
                rootPath,
                Array.Empty<ChangeFeedEventDto>(),
                WidestGap,
                WidestFault,
                false,
                false)) + SeparatorBytes;
    }

    public long Event(ChangeFeedEvent change)
    {
        ArgumentNullException.ThrowIfNull(change);

        return ChangeFeedMessageChannel.MeasureResponse(
            ChangeFeedDeliveryContract.ToWire(change)) + SeparatorBytes;
    }
}
