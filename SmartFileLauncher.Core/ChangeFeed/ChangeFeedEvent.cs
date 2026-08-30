namespace SmartFileLauncher.Core.ChangeFeed;

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

public enum ChangeFeedEventKind
{
    Created,
    Deleted,
    Renamed,
    Modified
}
