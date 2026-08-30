namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// Reads a moved-in subtree from the file system with the same walk the
/// baseline map uses.
/// </summary>
/// <remarks>
/// The walk is best effort: a directory that cannot be listed is counted and
/// skipped rather than failing the batch. Failing instead would stall the feed,
/// because a gap prevents the accept that would move past the very records that
/// triggered the walk.
/// </remarks>
public sealed class UsnFileSystemSubtreeReader : IUsnSubtreeReader
{
    private readonly IUsnIdentityProbe _identityProbe;
    private readonly Func<string, string[]> _listDirectories;

    public UsnFileSystemSubtreeReader(IUsnIdentityProbe identityProbe)
        : this(identityProbe, UsnDirectoryWalk.ListDirectories)
    {
    }

    internal UsnFileSystemSubtreeReader(
        IUsnIdentityProbe identityProbe,
        Func<string, string[]> listDirectories)
    {
        _identityProbe = identityProbe ?? throw new ArgumentNullException(nameof(identityProbe));
        _listDirectories = listDirectories ?? throw new ArgumentNullException(nameof(listDirectories));
    }

    public UsnSubtreeReadResult ReadSubtree(
        string directoryPath,
        UsnFileReference directoryReference,
        ulong volumeSerialNumber,
        CancellationToken cancellationToken = default)
    {
        if (string.IsNullOrWhiteSpace(directoryPath) || directoryReference.IsNone)
        {
            return UsnSubtreeReadResult.Empty;
        }

        var directories = new List<UsnDirectoryEntry>();
        var skipped = UsnDirectoryWalk.Walk(
            directoryPath,
            directoryReference,
            volumeSerialNumber,
            _identityProbe,
            _listDirectories,
            directories,
            cancellationToken);

        return new UsnSubtreeReadResult(directories, skipped);
    }
}
