namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed class UsnRootProjection
{
    private readonly IUsnIdentityProbe _identityProbe;
    private readonly IUsnSubtreeReader _subtreeReader;
    private readonly UsnDirectoryMap _directories;
    private UsnProjectionScope? _pendingScope;

    public UsnRootProjection(
        UsnChangeFeedState state,
        IUsnIdentityProbe identityProbe,
        IUsnSubtreeReader? subtreeReader = null)
    {
        ArgumentNullException.ThrowIfNull(state);
        _identityProbe = identityProbe ?? throw new ArgumentNullException(nameof(identityProbe));
        _subtreeReader = subtreeReader ?? new UsnFileSystemSubtreeReader(_identityProbe);

        RootPath = state.RootPath;
        RootIdentity = state.ToChangeFeedRootIdentity();
        VolumeSerialNumber = state.RootIdentity.VolumeSerialNumber;
        _directories = new UsnDirectoryMap(state.RootPath, state.RootIdentity.FileReference);

        foreach (var entry in state.Directories)
        {
            _directories.Set(entry.Reference, entry.Name, entry.ParentReference);
        }
    }

    public string RootPath { get; }

    public ChangeFeedRootIdentity RootIdentity { get; }

    public ulong VolumeSerialNumber { get; }

    public int LastSkippedSubtreeDirectoryCount { get; private set; }

    internal int DirectoryCount => _directories.Count;

    public ChangeFeedGapReason CheckRoot()
    {
        if (!_identityProbe.TryReadIdentity(RootPath, out var identity))
        {
            return ChangeFeedGapReason.RootUnavailable;
        }

        if (identity.VolumeSerialNumber != VolumeSerialNumber ||
            identity.FileReference != _directories.RootReference)
        {
            return ChangeFeedGapReason.RootIdentityChanged;
        }

        return ChangeFeedGapReason.None;
    }

    public ChangeFeedBatch Project(
        IReadOnlyList<UsnRecord> records,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(records);
        _pendingScope = null;

        var scope = new UsnProjectionScope(_directories);
        var projection = UsnEventProjector.Project(
            new UsnProjectionContext(
                scope,
                _subtreeReader,
                VolumeSerialNumber,
                cancellationToken),
            records);

        if (projection.GapReason != ChangeFeedGapReason.None)
        {
            return ChangeFeedBatch.Gap(projection.GapReason);
        }

        LastSkippedSubtreeDirectoryCount = projection.SkippedSubtreeDirectoryCount;
        _pendingScope = scope;
        return ChangeFeedBatch.Ok(projection.Events);
    }

    public void Discard() => _pendingScope = null;

    public void Accept()
    {
        _pendingScope?.Commit();
        _pendingScope = null;
    }

    public UsnChangeFeedState CaptureState(ulong journalId, long nextUsn) =>
        new(
            RootPath,
            new UsnNodeIdentity(VolumeSerialNumber, _directories.RootReference),
            journalId,
            nextUsn,
            _directories.Entries.ToArray());
}
