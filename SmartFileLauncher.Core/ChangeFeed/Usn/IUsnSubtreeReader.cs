namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// Learns the directory identities of a subtree that entered a feed root
/// without producing journal records of its own.
/// </summary>
/// <remarks>
/// <para>
/// Moving a directory renames one entry; its descendants are untouched on disk
/// and therefore silent in the journal. Without this walk the feed would accept
/// the move and then fail to resolve every later change under it.
/// </para>
/// <para>
/// Implementations must stay inside the root: a reparse point is reported as a
/// skipped directory, never traversed, so directories that live outside the root
/// cannot enter the feed map through a junction.
/// </para>
/// </remarks>
public interface IUsnSubtreeReader
{
    UsnSubtreeReadResult ReadSubtree(
        string directoryPath,
        UsnFileReference directoryReference,
        ulong volumeSerialNumber,
        CancellationToken cancellationToken = default);
}

public sealed record UsnSubtreeReadResult(
    IReadOnlyList<UsnDirectoryEntry> Directories,
    int SkippedDirectoryCount)
{
    public static readonly UsnSubtreeReadResult Empty =
        new(Array.Empty<UsnDirectoryEntry>(), 0);
}
