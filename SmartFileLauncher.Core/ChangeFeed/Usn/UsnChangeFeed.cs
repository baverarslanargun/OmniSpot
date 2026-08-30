namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// A <see cref="IChangeFeed"/> backed by the NTFS/ReFS USN change journal.
/// </summary>
/// <remarks>
/// The batch, the journal cursor and the directory map advance as one unit in
/// <see cref="Accept"/>. Committing the map earlier would make a replayed batch
/// resolve rename records against already-updated names, so the map belongs on
/// the cursor side of the commit, after the index write.
/// </remarks>
public sealed class UsnChangeFeed : IChangeFeed
{
    public const string ProviderIdentifier = "usn";

    private readonly IUsnJournalReader _journalReader;
    private readonly IUsnIdentityProbe _identityProbe;
    private readonly IUsnSubtreeReader _subtreeReader;
    private readonly UsnDirectoryMap _directories;
    private readonly ulong _volumeSerialNumber;
    private readonly bool _ownsJournalReader;

    private ulong _journalId;
    private long _nextUsn;
    private UsnProjectionScope? _pendingScope;
    private long _pendingNextUsn;
    private bool _disposed;

    public UsnChangeFeed(
        UsnChangeFeedState state,
        IUsnJournalReader journalReader,
        IUsnIdentityProbe identityProbe,
        IUsnSubtreeReader? subtreeReader = null,
        bool ownsJournalReader = false)
    {
        ArgumentNullException.ThrowIfNull(state);
        _journalReader = journalReader ?? throw new ArgumentNullException(nameof(journalReader));
        _identityProbe = identityProbe ?? throw new ArgumentNullException(nameof(identityProbe));
        _subtreeReader = subtreeReader ?? new UsnFileSystemSubtreeReader(_identityProbe);
        _ownsJournalReader = ownsJournalReader;

        RootPath = state.RootPath;
        RootIdentity = state.ToChangeFeedRootIdentity();
        _volumeSerialNumber = state.RootIdentity.VolumeSerialNumber;
        _journalId = state.JournalId;
        _nextUsn = state.NextUsn;
        _directories = new UsnDirectoryMap(state.RootPath, state.RootIdentity.FileReference);

        foreach (var entry in state.Directories)
        {
            _directories.Set(entry.Reference, entry.Name, entry.ParentReference);
        }
    }

    public string ProviderId => ProviderIdentifier;

    public string RootPath { get; }

    public ChangeFeedRootIdentity RootIdentity { get; }

    /// <summary>Journal position that the last <see cref="Accept"/> committed.</summary>
    public long AcceptedUsn => _nextUsn;

    /// <summary>
    /// Directories a moved-in subtree could not contribute to the map during the
    /// last successful read. Changes below them cannot be resolved, so a
    /// non-zero value belongs in the reconciliation diagnostics.
    /// </summary>
    public int LastSkippedSubtreeDirectoryCount { get; private set; }

    internal int DirectoryCount => _directories.Count;

    public ChangeFeedBatch Read(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _pendingScope = null;

        UsnJournalDescriptor descriptor;
        try
        {
            descriptor = _journalReader.QueryJournal();
        }
        catch (UsnJournalUnavailableException)
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.JournalUnavailable);
        }

        if (descriptor.JournalId != _journalId)
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.JournalIdChanged);
        }

        if (_nextUsn < descriptor.FirstUsn || _nextUsn > descriptor.NextUsn)
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.CursorOutsideJournal);
        }

        if (!_identityProbe.TryReadIdentity(RootPath, out var identity))
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.RootUnavailable);
        }

        if (identity.VolumeSerialNumber != _volumeSerialNumber ||
            identity.FileReference != _directories.RootReference)
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.RootIdentityChanged);
        }

        var endUsn = descriptor.NextUsn;
        List<UsnRecord> records;
        try
        {
            records = ReadRecords(endUsn, cancellationToken);
        }
        catch (UsnJournalUnavailableException)
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.JournalUnavailable);
        }
        catch (UsnRecordFormatException)
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.FeedStateInvalid);
        }

        var scope = new UsnProjectionScope(_directories);
        var projection = UsnEventProjector.Project(
            new UsnProjectionContext(
                scope,
                _subtreeReader,
                _volumeSerialNumber,
                cancellationToken),
            records);

        if (projection.GapReason != ChangeFeedGapReason.None)
        {
            return ChangeFeedBatch.Gap(projection.GapReason);
        }

        LastSkippedSubtreeDirectoryCount = projection.SkippedSubtreeDirectoryCount;
        _pendingScope = scope;
        _pendingNextUsn = endUsn;
        return ChangeFeedBatch.Ok(projection.Events);
    }

    public void Accept()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_pendingScope is null)
        {
            return;
        }

        _pendingScope.Commit();
        _nextUsn = _pendingNextUsn;
        _pendingScope = null;
    }

    public UsnChangeFeedState CaptureState() =>
        new(
            RootPath,
            new UsnNodeIdentity(_volumeSerialNumber, _directories.RootReference),
            _journalId,
            _nextUsn,
            _directories.Entries.ToArray());

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingScope = null;

        if (_ownsJournalReader)
        {
            _journalReader.Dispose();
        }
    }

    private List<UsnRecord> ReadRecords(long endUsn, CancellationToken cancellationToken)
    {
        var records = new List<UsnRecord>();
        var cursor = _nextUsn;

        while (cursor < endUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = _journalReader.ReadPage(cursor, _journalId);
            if (page.NextUsn <= cursor)
            {
                throw new UsnJournalUnavailableException(
                    $"USN imleci ilerlemedi: {cursor} -> {page.NextUsn}.");
            }

            UsnRecordParser.Parse(page.Records.Span, records);
            cursor = page.NextUsn;
        }

        // The accepted cursor is the queried end, so anything at or past it must
        // stay for the next batch instead of being delivered twice.
        records.RemoveAll(record => record.Usn >= endUsn);
        return records;
    }
}
