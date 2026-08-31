namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed record UsnVolumeRootBatch(UsnRootProjection Root, ChangeFeedBatch Batch);

public sealed record UsnVolumeBatch(long NextUsn, IReadOnlyList<UsnVolumeRootBatch> Roots);

public sealed class UsnVolumeChangeFeed : IDisposable
{
    private readonly IUsnJournalReader _journalReader;
    private readonly IReadOnlyList<UsnRootProjection> _roots;
    private readonly bool _ownsJournalReader;

    private ChangeFeedBatch? _fault;
    private long? _pendingNextUsn;
    private bool _disposed;

    public UsnVolumeChangeFeed(
        IUsnJournalReader journalReader,
        ulong journalId,
        long nextUsn,
        IReadOnlyList<UsnRootProjection> roots,
        bool ownsJournalReader = false)
    {
        _journalReader = journalReader ?? throw new ArgumentNullException(nameof(journalReader));
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegative(nextUsn);

        if (roots.Count == 0)
        {
            throw new ArgumentException("En az bir kök gerekiyor.", nameof(roots));
        }

        _roots = roots.ToArray();
        _ownsJournalReader = ownsJournalReader;
        JournalId = journalId;
        AcceptedUsn = nextUsn;
    }

    public ulong JournalId { get; }

    public long AcceptedUsn { get; private set; }

    public IReadOnlyList<UsnRootProjection> Roots => _roots;

    public bool IsFaulted => _fault is not null;

    public static UsnVolumeChangeFeed Create(
        IUsnJournalReader journalReader,
        IReadOnlyList<UsnChangeFeedState> states,
        IUsnIdentityProbe identityProbe,
        IUsnSubtreeReader? subtreeReader = null,
        bool ownsJournalReader = false)
    {
        ArgumentNullException.ThrowIfNull(states);

        if (states.Count == 0)
        {
            throw new ArgumentException("En az bir kök durumu gerekiyor.", nameof(states));
        }

        var journalId = states[0].JournalId;
        var volumeSerialNumber = states[0].RootIdentity.VolumeSerialNumber;
        var cursor = states[0].NextUsn;

        foreach (var state in states)
        {
            if (state.JournalId != journalId)
            {
                throw new ArgumentException(
                    "Aynı birimdeki köklerin günlük kimliği eşleşmiyor.",
                    nameof(states));
            }

            if (state.RootIdentity.VolumeSerialNumber != volumeSerialNumber)
            {
                throw new ArgumentException("Kökler aynı birimde değil.", nameof(states));
            }

            cursor = Math.Min(cursor, state.NextUsn);
        }

        var roots = states
            .Select(state => new UsnRootProjection(state, identityProbe, subtreeReader))
            .ToArray();

        return new UsnVolumeChangeFeed(
            journalReader,
            journalId,
            cursor,
            roots,
            ownsJournalReader);
    }

    public UsnVolumeBatch Read(CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        _pendingNextUsn = null;
        foreach (var root in _roots)
        {
            root.Discard();
        }

        if (_fault is not null)
        {
            return Uniform(_fault);
        }

        UsnJournalDescriptor descriptor;
        try
        {
            descriptor = _journalReader.QueryJournal();
        }
        catch (UsnJournalUnavailableException)
        {
            return Uniform(ChangeFeedBatch.Gap(ChangeFeedGapReason.JournalUnavailable));
        }
        catch (UsnProtocolRejectedException rejection)
        {
            return Uniform(Fault(rejection));
        }

        if (descriptor.JournalId != JournalId)
        {
            return Uniform(ChangeFeedBatch.Gap(ChangeFeedGapReason.JournalIdChanged));
        }

        if (AcceptedUsn < descriptor.FirstUsn || AcceptedUsn > descriptor.NextUsn)
        {
            return Uniform(ChangeFeedBatch.Gap(ChangeFeedGapReason.CursorOutsideJournal));
        }

        var results = new UsnVolumeRootBatch?[_roots.Count];
        var readable = new List<int>(_roots.Count);

        for (var index = 0; index < _roots.Count; index++)
        {
            var rootGap = _roots[index].CheckRoot();
            if (rootGap == ChangeFeedGapReason.None)
            {
                readable.Add(index);
                continue;
            }

            results[index] = new UsnVolumeRootBatch(_roots[index], ChangeFeedBatch.Gap(rootGap));
        }

        if (readable.Count == 0)
        {
            return new UsnVolumeBatch(AcceptedUsn, Materialize(results));
        }

        var endUsn = descriptor.NextUsn;
        List<UsnRecord> records;
        try
        {
            records = ReadRecords(endUsn, cancellationToken);
        }
        catch (UsnJournalUnavailableException)
        {
            return Fill(results, readable, ChangeFeedBatch.Gap(ChangeFeedGapReason.JournalUnavailable));
        }
        catch (UsnRecordFormatException)
        {
            return Fill(results, readable, ChangeFeedBatch.Gap(ChangeFeedGapReason.FeedStateInvalid));
        }
        catch (UsnProtocolRejectedException rejection)
        {
            return Fill(results, readable, ClassifyRejection(rejection));
        }

        var accepted = false;
        foreach (var index in readable)
        {
            var root = _roots[index];
            var batch = root.Project(records, cancellationToken);
            results[index] = new UsnVolumeRootBatch(root, batch);
            accepted |= batch.Status == ChangeFeedStatus.Ok;
        }

        if (!accepted)
        {
            return new UsnVolumeBatch(AcceptedUsn, Materialize(results));
        }

        _pendingNextUsn = endUsn;
        return new UsnVolumeBatch(endUsn, Materialize(results));
    }

    public void Accept()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (_pendingNextUsn is null)
        {
            return;
        }

        foreach (var root in _roots)
        {
            root.Accept();
        }

        AcceptedUsn = _pendingNextUsn.Value;
        _pendingNextUsn = null;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _pendingNextUsn = null;

        foreach (var root in _roots)
        {
            root.Discard();
        }

        if (_ownsJournalReader)
        {
            _journalReader.Dispose();
        }
    }

    private ChangeFeedBatch ClassifyRejection(UsnProtocolRejectedException rejection)
    {
        UsnJournalDescriptor descriptor;
        try
        {
            descriptor = _journalReader.QueryJournal();
        }
        catch (UsnJournalUnavailableException)
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.JournalUnavailable);
        }
        catch (UsnProtocolRejectedException nested)
        {
            return Fault(nested);
        }

        if (descriptor.JournalId != JournalId)
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.JournalIdChanged);
        }

        if (AcceptedUsn < descriptor.FirstUsn || AcceptedUsn > descriptor.NextUsn)
        {
            return ChangeFeedBatch.Gap(ChangeFeedGapReason.CursorOutsideJournal);
        }

        return Fault(rejection);
    }

    private ChangeFeedBatch Fault(UsnProtocolRejectedException rejection)
    {
        _fault = ChangeFeedBatch.Faulted(
            ChangeFeedFaultReason.NativeProtocolRejected,
            rejection.Message);

        return _fault;
    }

    private UsnVolumeBatch Uniform(ChangeFeedBatch batch) =>
        new(
            AcceptedUsn,
            _roots.Select(root => new UsnVolumeRootBatch(root, batch)).ToArray());

    private UsnVolumeBatch Fill(
        UsnVolumeRootBatch?[] results,
        List<int> readable,
        ChangeFeedBatch batch)
    {
        foreach (var index in readable)
        {
            results[index] = new UsnVolumeRootBatch(_roots[index], batch);
        }

        return new UsnVolumeBatch(AcceptedUsn, Materialize(results));
    }

    private static IReadOnlyList<UsnVolumeRootBatch> Materialize(UsnVolumeRootBatch?[] results) =>
        results.Select(result => result!).ToArray();

    private List<UsnRecord> ReadRecords(long endUsn, CancellationToken cancellationToken)
    {
        var records = new List<UsnRecord>();
        var cursor = AcceptedUsn;

        while (cursor < endUsn)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var page = _journalReader.ReadPage(cursor, JournalId);
            if (page.NextUsn <= cursor)
            {
                throw new UsnJournalUnavailableException(
                    $"USN imleci ilerlemedi: {cursor} -> {page.NextUsn}.");
            }

            UsnRecordParser.Parse(page.Records.Span, records);
            cursor = page.NextUsn;
        }

        records.RemoveAll(record => record.Usn >= endUsn);
        return records;
    }
}
