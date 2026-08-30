namespace SmartFileLauncher.Core.ChangeFeed.Usn;

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
