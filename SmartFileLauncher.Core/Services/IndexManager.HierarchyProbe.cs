using System.Runtime.CompilerServices;
using SmartFileLauncher.Core.Search;

[assembly: InternalsVisibleTo("OmniSpot.Benchmarking")]

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager
{
    internal sealed record HierarchyProbeEntry(SearchItem Item, long ModifiedUtc, long CreatedUtc,
        bool Hidden, bool System);

    internal string[] CaptureHierarchyProbe(string root, Action<HierarchyProbeEntry> sink, CancellationToken ct)
    {
        var normalized = NormalizeIndexedPath(root);
        var result = CaptureDiskSnapshot([normalized], ct, followReparsePoints: true, entrySink: entry =>
        {
            var parent = string.Equals(entry.Path, normalized, StringComparison.OrdinalIgnoreCase)
                ? "" : Path.GetDirectoryName(entry.Path);
            var item = entry.IsDirectory
                ? CompactDirectoryItem(PathName(entry.Path), entry.Path, parent)
                : new SearchItem(PathName(entry.Path), entry.Path, false, entry.SizeBytes,
                    new DateTime(entry.CreatedTimeUtc, DateTimeKind.Utc).ToLocalTime(),
                    new DateTime(entry.LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime(), 0, parent);
            try
            {
                sink(new(item, entry.LastWriteTimeUtc, entry.CreatedTimeUtc,
                    (entry.Attributes & FileAttributes.Hidden) != 0,
                    (entry.Attributes & FileAttributes.System) != 0));
            }
            catch (IOException error)
            {
                throw new InvalidOperationException("Prototip kayıt yazıcısı başarısız oldu.", error);
            }
        });
        return result.Errors.ToArray();
    }

    internal async Task<string[]> BootstrapHierarchyProbeAsync(string root,
        IEnumerable<HierarchyProbeEntry>? replay, CancellationToken ct)
    {
        _db.Open();
        ReconciliationSnapshot? snapshot = null;
        if (replay is null)
        {
            ReportProgress("Seçili klasörler taranıyor...", 0, 0, 0,
                isIndeterminate: true, phase: "directory_inventory");
            snapshot = CaptureDiskSnapshot([NormalizeIndexedPath(root)], ct, followReparsePoints: true);
        }
        if (replay is not null)
        {
            snapshot = new();
            foreach (var entry in replay)
            {
                ct.ThrowIfCancellationRequested();
                snapshot.Capture(new(entry.Item.FullPath, entry.Item.IsDirectory, entry.ModifiedUtc,
                    entry.Item.SizeBytes ?? 0,
                    (entry.Hidden ? FileAttributes.Hidden : 0) | (entry.System ? FileAttributes.System : 0),
                    entry.CreatedUtc));
            }
        }
        await BootstrapCompactScanCoreAsync([NormalizeIndexedPath(root)], null, ct, snapshot);
        return snapshot!.Errors.ToArray();
    }
}
