using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

internal sealed class NtfsInventorySecurityGuard : IDisposable
{
    private readonly Func<string, IUsnJournalReader> _open;
    private readonly Dictionary<string, Volume> _volumes = new(StringComparer.OrdinalIgnoreCase);

    public NtfsInventorySecurityGuard(Func<string, IUsnJournalReader>? open = null) =>
        _open = open ?? (path => new UsnVolumeJournalReader(path));

    public void BeginVolume(string path)
    {
        var reader = _open(path);
        try { _volumes.Add(path, new Volume(reader, reader.QueryJournal())); }
        catch { reader.Dispose(); throw; }
    }

    public void Include(string volume, ulong reference) => _volumes[volume].References.Add(reference);

    public bool Validate(CancellationToken ct)
    {
        foreach (var volume in _volumes.Values)
        {
            ct.ThrowIfCancellationRequested();
            var current = volume.Reader.QueryJournal();
            var start = volume.Start.NextUsn;
            if (current.JournalId != volume.Start.JournalId || current.NextUsn < start ||
                start < Math.Max(current.FirstUsn, current.LowestValidUsn)) return false;
            var cursor = start;
            while (cursor < current.NextUsn)
            {
                ct.ThrowIfCancellationRequested();
                var page = volume.Reader.ReadPage(cursor, current.JournalId);
                if (page.NextUsn <= cursor) return false;
                foreach (var record in UsnRecordParser.Parse(page.Records.Span))
                {
                    if (record.Usn < cursor || record.Usn >= current.NextUsn) continue;
                    if ((record.Reason & (UsnReason.SecurityChange | UsnReason.ReparsePointChange)) != 0 &&
                        (record.FileReference.High != 0 || volume.References.Contains(record.FileReference.Low)))
                        return false;
                }
                cursor = page.NextUsn;
            }
        }
        return _volumes.Count > 0;
    }

    public void Dispose()
    {
        foreach (var volume in _volumes.Values) volume.Reader.Dispose();
        _volumes.Clear();
    }

    private sealed record Volume(IUsnJournalReader Reader, UsnJournalDescriptor Start)
    {
        public HashSet<ulong> References { get; } = [];
    }
}
