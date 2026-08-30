namespace SmartFileLauncher.Core.ChangeFeed.Usn;

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
