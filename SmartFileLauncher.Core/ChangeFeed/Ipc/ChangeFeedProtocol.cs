namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public static class ChangeFeedProtocol
{
    public const int Version = 1;

    public const string PipeName = "OmniSpot.ChangeFeed";

    public const int MaximumRequestBytes = 64 * 1024;

    public const int MaximumResponseBytes = 1024 * 1024;

    public const int PipeOutboundBufferBytes = 64 * 1024;

    public const int PipeInboundBufferBytes = 64 * 1024;

    public const int MaximumConcurrentConnections = 4;

    public const int MaximumPipeInstances = MaximumConcurrentConnections * 2;

    public const int LengthPrefixBytes = 4;

    public static TimeSpan IoTimeout => TimeSpan.FromSeconds(5);
}

public enum ChangeFeedRequestKind
{
    AddRoot,
    RemoveRoot,
    ListRoots
}

public enum ChangeFeedResponseStatus
{
    Ok,
    VersionMismatch,
    InvalidRequest,
    RootUnauthorized,
    RootUnusable,
    Unavailable
}

public sealed record ChangeFeedRequest(
    int Version,
    ChangeFeedRequestKind Kind,
    string? RootPath = null);

public sealed record ChangeFeedResponse(
    int Version,
    ChangeFeedResponseStatus Status,
    string? Message = null,
    IReadOnlyList<string>? Roots = null)
{
    public static ChangeFeedResponse Ok(IReadOnlyList<string>? roots = null) =>
        new(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok, null, roots);

    public static ChangeFeedResponse Failed(ChangeFeedResponseStatus status, string message) =>
        new(ChangeFeedProtocol.Version, status, message);
}
