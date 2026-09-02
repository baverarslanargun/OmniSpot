namespace SmartFileLauncher.Core.ChangeFeed.Store;

public sealed class ChangeFeedQueueEntry
{
    public ChangeFeedQueueEntry(
        long sequence,
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> roots)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(sequence);
        ArgumentNullException.ThrowIfNull(volumeId);
        ArgumentOutOfRangeException.ThrowIfNegative(fromUsn);
        ArgumentOutOfRangeException.ThrowIfLessThan(toUsn, fromUsn);
        ArgumentNullException.ThrowIfNull(roots);

        if (roots.Count == 0)
        {
            throw new ArgumentException("Kuyruk girdisi en az bir kök içermelidir.", nameof(roots));
        }

        Sequence = sequence;
        VolumeId = volumeId;
        JournalId = journalId;
        FromUsn = fromUsn;
        ToUsn = toUsn;
        Roots = roots.ToArray();
    }

    public long Sequence { get; }

    public string VolumeId { get; }

    public ulong JournalId { get; }

    public long FromUsn { get; }

    public long ToUsn { get; }

    public IReadOnlyList<ChangeFeedRootDelivery> Roots { get; }

    public bool IsPositional => ToUsn > 0;

    public bool HasAnyGap => Roots.Any(root => root.Batch.HasGap);

    public int EventCount => Roots.Sum(root => root.Batch.Events.Count);
}

public sealed class ChangeFeedRootDelivery
{
    public ChangeFeedRootDelivery(string rootPath, ChangeFeedBatch batch)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath;
        Batch = batch ?? throw new ArgumentNullException(nameof(batch));
    }

    public string RootPath { get; }

    public ChangeFeedBatch Batch { get; }
}
