using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

internal sealed class NtfsInventorySecurityGuard : IDisposable
{
    private readonly Func<string, IUsnJournalReader> _open;
    private readonly Dictionary<string, Volume> _volumes = new(StringComparer.OrdinalIgnoreCase);
    public string? LastFailure { get; private set; }

    public NtfsInventorySecurityGuard(Func<string, IUsnJournalReader>? open = null) =>
        _open = open ?? (path => new UsnVolumeJournalReader(path));

    public void BeginVolume(string path)
    {
        var reader = _open(path);
        try { _volumes.Add(path, new Volume(reader, reader.QueryJournal())); }
        catch { reader.Dispose(); throw; }
    }

    public void Include(string volume, ulong reference) => _volumes[volume].References.Add(reference);

    public void ExpectDirectoryChange(string volume, ulong reference)
    {
        _volumes[volume].ExpectedDirectoryChanges.Add(reference);
        Include(volume, reference);
    }

    public bool Validate(CancellationToken ct)
    {
        LastFailure = null;
        foreach (var (path, volume) in _volumes)
        {
            ct.ThrowIfCancellationRequested();
            var current = volume.Reader.QueryJournal();
            var start = volume.Start.NextUsn;
            if (current.JournalId != volume.Start.JournalId || current.NextUsn < start ||
                start < Math.Max(current.FirstUsn, current.LowestValidUsn))
                return Reject($"USN sürekliliği kayboldu: cilt={path}; başlangıç={start}; ilk={current.FirstUsn}; geçerli={current.LowestValidUsn}; son={current.NextUsn}; günlük={volume.Start.JournalId:X}/{current.JournalId:X}.");
            var cursor = start;
            var observedDirectoryChanges = new HashSet<ulong>();
            while (cursor < current.NextUsn)
            {
                ct.ThrowIfCancellationRequested();
                var page = volume.Reader.ReadPage(cursor, current.JournalId);
                if (page.NextUsn <= cursor) return Reject($"USN sayfası ilerlemedi: cilt={path}; imleç={cursor}; sonraki={page.NextUsn}.");
                foreach (var record in UsnRecordParser.Parse(page.Records.Span))
                {
                    if (record.Usn < cursor || record.Usn >= current.NextUsn) continue;
                    if (record.IsDirectory && record.FileReference.High == 0 &&
                        (record.Reason & (UsnReason.FileCreate | UsnReason.FileDelete |
                            UsnReason.RenameOldName | UsnReason.RenameNewName)) != 0)
                        observedDirectoryChanges.Add(record.FileReference.Low);
                    var invalidatingReasons = record.IsDirectory || record.FileReference.High != 0
                        ? UsnReason.SecurityChange | UsnReason.ReparsePointChange
                        : UsnReason.ReparsePointChange;
                    if ((record.Reason & invalidatingReasons) != 0 &&
                        (record.FileReference.High != 0 || volume.References.Contains(record.FileReference.Low)))
                        return Reject($"NTFS güvenlik/reparse değişikliği: cilt={path}; FRN={record.FileReference.High:X16}:{record.FileReference.Low:X16}; USN={record.Usn}; neden={record.Reason}; dizin={record.IsDirectory}; başlangıç={start}; son={current.NextUsn}.");
                }
                cursor = page.NextUsn;
            }
            if (!volume.ExpectedDirectoryChanges.IsSubsetOf(observedDirectoryChanges))
                return Reject($"Dizin değişikliğinin USN kanıtı eksik: cilt={path}; FRN={volume.ExpectedDirectoryChanges.First(reference => !observedDirectoryChanges.Contains(reference)):X16}; beklenen={volume.ExpectedDirectoryChanges.Count}; görülen={observedDirectoryChanges.Count}; başlangıç={start}; son={current.NextUsn}.");
        }
        return _volumes.Count > 0 || Reject("Doğrulanacak NTFS cildi yok.");
    }

    private bool Reject(string reason)
    {
        LastFailure = reason;
        return false;
    }

    public void Dispose()
    {
        foreach (var volume in _volumes.Values) volume.Reader.Dispose();
        _volumes.Clear();
    }

    private sealed record Volume(IUsnJournalReader Reader, UsnJournalDescriptor Start)
    {
        public HashSet<ulong> References { get; } = [];
        public HashSet<ulong> ExpectedDirectoryChanges { get; } = [];
    }
}
