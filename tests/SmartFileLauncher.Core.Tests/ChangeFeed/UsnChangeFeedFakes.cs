using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

internal sealed class FakeUsnJournalReader : IUsnJournalReader
{
    private readonly Queue<UsnReadPage> _pages = new();

    public UsnJournalDescriptor Descriptor { get; set; }

    public bool QueryFails { get; set; }

    public List<(long StartUsn, ulong JournalId)> ReadCalls { get; } = new();

    public bool Disposed { get; private set; }

    public FakeUsnJournalReader EnqueuePage(long nextUsn, byte[] records)
    {
        _pages.Enqueue(new UsnReadPage(nextUsn, records));
        return this;
    }

    public UsnJournalDescriptor QueryJournal()
    {
        if (QueryFails)
        {
            throw new UsnJournalUnavailableException("Test: günlük okunamadı.");
        }

        return Descriptor;
    }

    public UsnReadPage ReadPage(long startUsn, ulong journalId)
    {
        ReadCalls.Add((startUsn, journalId));

        if (_pages.Count == 0)
        {
            throw new InvalidOperationException(
                $"Test: {startUsn} için hazırlanmış sayfa yok.");
        }

        return _pages.Dequeue();
    }

    public void Dispose() => Disposed = true;
}

internal sealed class FakeUsnIdentityProbe : IUsnIdentityProbe
{
    private readonly Dictionary<string, UsnNodeIdentity> _identities =
        new(StringComparer.OrdinalIgnoreCase);

    public FakeUsnIdentityProbe Set(string path, ulong volumeSerialNumber, ulong fileReference)
    {
        _identities[path] = new UsnNodeIdentity(
            volumeSerialNumber,
            UsnFileReference.FromNtfs(fileReference));
        return this;
    }

    public FakeUsnIdentityProbe Remove(string path)
    {
        _identities.Remove(path);
        return this;
    }

    public bool TryReadIdentity(string path, out UsnNodeIdentity identity) =>
        _identities.TryGetValue(path, out identity);
}

internal sealed class FakeUsnSubtreeReader : IUsnSubtreeReader
{
    private readonly Dictionary<string, UsnSubtreeReadResult> _subtrees =
        new(StringComparer.OrdinalIgnoreCase);

    public List<string> Requests { get; } = new();

    public FakeUsnSubtreeReader Add(
        string directoryPath,
        int skippedDirectoryCount,
        params UsnDirectoryEntry[] directories)
    {
        _subtrees[directoryPath] = new UsnSubtreeReadResult(directories, skippedDirectoryCount);
        return this;
    }

    public UsnSubtreeReadResult ReadSubtree(
        string directoryPath,
        UsnFileReference directoryReference,
        ulong volumeSerialNumber,
        CancellationToken cancellationToken = default)
    {
        Requests.Add(directoryPath);

        return _subtrees.TryGetValue(directoryPath, out var result)
            ? result
            : UsnSubtreeReadResult.Empty;
    }
}
