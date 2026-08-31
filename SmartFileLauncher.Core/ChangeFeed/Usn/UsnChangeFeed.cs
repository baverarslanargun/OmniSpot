namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed class UsnChangeFeed : IChangeFeed
{
    public const string ProviderIdentifier = "usn";

    private readonly UsnVolumeChangeFeed _volume;
    private readonly UsnRootProjection _root;

    public UsnChangeFeed(
        UsnChangeFeedState state,
        IUsnJournalReader journalReader,
        IUsnIdentityProbe identityProbe,
        IUsnSubtreeReader? subtreeReader = null,
        bool ownsJournalReader = false)
    {
        ArgumentNullException.ThrowIfNull(state);

        _root = new UsnRootProjection(state, identityProbe, subtreeReader);
        _volume = new UsnVolumeChangeFeed(
            journalReader,
            state.JournalId,
            state.NextUsn,
            new[] { _root },
            ownsJournalReader);
    }

    public string ProviderId => ProviderIdentifier;

    public string RootPath => _root.RootPath;

    public ChangeFeedRootIdentity RootIdentity => _root.RootIdentity;

    public long AcceptedUsn => _volume.AcceptedUsn;

    public bool IsFaulted => _volume.IsFaulted;

    public int LastSkippedSubtreeDirectoryCount => _root.LastSkippedSubtreeDirectoryCount;

    internal int DirectoryCount => _root.DirectoryCount;

    public ChangeFeedBatch Read(CancellationToken cancellationToken = default) =>
        _volume.Read(cancellationToken).Roots[0].Batch;

    public void Accept() => _volume.Accept();

    public UsnChangeFeedState CaptureState() =>
        _root.CaptureState(_volume.JournalId, _volume.AcceptedUsn);

    public void Dispose() => _volume.Dispose();
}
