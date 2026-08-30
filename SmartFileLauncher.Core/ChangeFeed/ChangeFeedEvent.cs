namespace SmartFileLauncher.Core.ChangeFeed;

/// <summary>
/// A single change reported by a change feed, already resolved to a full path
/// inside the feed root.
/// </summary>
public sealed record ChangeFeedEvent(
    ChangeFeedEventKind Kind,
    string FullPath,
    bool IsDirectory,
    string? OldPath = null)
{
    public override string ToString() =>
        OldPath is null
            ? $"[{Kind}] {FullPath}"
            : $"[{Kind}] {OldPath} -> {FullPath}";
}

/// <summary>
/// Change kinds a feed can report. A directory event covers its whole subtree:
/// the consumer must rescan the subtree instead of assuming per-child events.
/// </summary>
public enum ChangeFeedEventKind
{
    Created,
    Deleted,
    Renamed,
    Modified
}
