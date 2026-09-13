using SmartFileLauncher.Core.Indexing;
using System.Diagnostics;

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager
{
    private IIndexInventorySession? _initialInventory;
    private bool _initialWatcherCapture;
    private long _watcherErrorVersion;
    private long _initialWatcherErrorVersion;
    private IReadOnlyList<string> _initialLinkScopes = Array.Empty<string>();
    private bool _initialInventoryPublished;
    private bool _initialLinksReconciled;
    private ReconciliationSnapshot? _initialLinkSnapshot;

    internal async Task<bool> ValidateInitialInventoryAsync(CancellationToken ct)
    {
        if (_initialInventory is null) return false;
        if (!InitialCaptureHealthy())
        {
            NotifyError("MFT doğrulaması başarısız: başlangıç dosya izleyicisi sağlıksız.");
            return false;
        }
        if (!await _initialInventory.ValidateAsync(ct).ConfigureAwait(false)) return false;
        if (_initialInventoryPublished && !_initialLinksReconciled && _initialLinkScopes.Count > 0)
        {
            bool reconciled;
            try
            {
                reconciled = await Task.Run(() =>
                {
                    var links = CaptureDiskSnapshot(_initialLinkScopes, ct, followReparsePoints: true);
                    if (links.Errors.Count != 0)
                    {
                        NotifyError($"MFT bağlantı devri başarısız ({links.Errors.Count} hata): {links.Errors[0]}");
                        return false;
                    }
                    var unchanged = _initialLinkSnapshot is { } previous &&
                        previous.Entries.Count == links.Entries.Count &&
                        previous.Entries.All(pair => links.Entries.TryGetValue(pair.Key, out var entry) && entry == pair.Value) &&
                        previous.ExcludedScopes.SetEquals(links.ExcludedScopes) &&
                        previous.ProtectedScopes.SetEquals(links.ProtectedScopes);
                    if (!unchanged)
                        ApplyCompactReconciliationSnapshot(_initialLinkScopes, links, ct, useSuppliedSnapshot: true);
                    return true;
                }, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception failure)
            {
                NotifyError($"Bağlantı envanteri uzlaştırılamadı: {failure.Message}");
                return false;
            }
            if (!reconciled) return false;
            _initialLinksReconciled = true;
            _initialLinkSnapshot = null;
            if (!await _initialInventory.ValidateAsync(ct).ConfigureAwait(false)) return false;
        }
        return InitialCaptureHealthy();
    }

    private bool InitialCaptureHealthy() => _initialWatcherCapture && _watcher.IsWatching &&
        Volatile.Read(ref _watcherErrorVersion) == _initialWatcherErrorVersion;

    private async Task<ReconciliationSnapshot?> ReadInitialInventoryAsync(
        IReadOnlyList<string> roots, IIndexInventorySource source, CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        long lastProgressMs = 0;
        ReportProgress("MFT dosya listesi alınıyor...", 0, 0, 0,
            isIndeterminate: true, phase: "inventory");
        _initialWatcherErrorVersion = Volatile.Read(ref _watcherErrorVersion);
        _initialLinkScopes = Array.Empty<string>();
        _initialInventoryPublished = false;
        _initialLinksReconciled = false;
        _initialLinkSnapshot = null;
        _initialWatcherCapture = SetupWatchers(roots, dispatchPaused: true, captureAllChanges: true);
        if (!InitialCaptureHealthy())
        {
            NotifyError("MFT edinimi başlayamadı: dosya izleyicisi kurulamadı.");
            return null;
        }
        var snapshot = new ReconciliationSnapshot();
        _initialInventory = await source.ReadAsync(roots, entry =>
        {
            var path = NormalizeIndexedPath(entry.Path);
            snapshot.Entries[path] = new ReconciliationEntry(path, entry.IsDirectory,
                entry.LastWriteTimeUtc, entry.SizeBytes, entry.Attributes, entry.CreatedTimeUtc);
            if (started.ElapsedMilliseconds - lastProgressMs >= 250)
            {
                lastProgressMs = started.ElapsedMilliseconds;
                ReportProgress("MFT dosya listesi alınıyor...", 0, snapshot.Entries.Count,
                    lastProgressMs, isIndeterminate: true, phase: "inventory");
            }
        }, ct).ConfigureAwait(false);
        if (!await ValidateInitialInventoryAsync(ct).ConfigureAwait(false)) return null;
        if (roots.Any(root => !snapshot.Entries.TryGetValue(root, out var entry) || !entry.IsDirectory))
        {
            NotifyError("MFT envanteri eksik: seçili köklerden en az biri bulunamadı.");
            return null;
        }

        _initialLinkScopes = NormalizeRootPaths(snapshot.Entries.Values
            .Where(entry => (entry.Attributes & FileAttributes.ReparsePoint) != 0)
            .Select(entry => entry.Path));
        if (_initialLinkScopes.Count > 0)
        {
            ReportProgress($"MFT envanteri alındı; {_initialLinkScopes.Count} bağlantı tamamlanıyor...",
                0, snapshot.Entries.Count, started.ElapsedMilliseconds,
                isIndeterminate: true, phase: "links");
            var links = await Task.Run(() =>
                CaptureDiskSnapshot(_initialLinkScopes, ct, followReparsePoints: true), ct).ConfigureAwait(false);
            if (links.Errors.Count != 0)
            {
                NotifyError($"MFT bağlantıları tamamlanamadı ({links.Errors.Count} hata): {links.Errors[0]}");
                return null;
            }
            foreach (var scope in _initialLinkScopes) snapshot.Entries.Remove(scope);
            foreach (var entry in links.Entries) snapshot.Entries[entry.Key] = entry.Value;
            MergeCompactSnapshotStatus(links, snapshot);
            _initialLinkSnapshot = links;
        }
        ReportProgress("Dosya listesi alındı", 0, snapshot.Entries.Count, started.ElapsedMilliseconds,
            isIndeterminate: true, phase: "inventory_complete");
        return snapshot;
    }
}
