using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager
{
    private int _liveResubscribeNeeded;
    internal bool LiveNeedsResubscribe => Volatile.Read(ref _liveResubscribeNeeded) != 0;

    private Task RebuildLiveRootAsync(string root, CancellationToken ct) => Task.Run(() =>
    {
        lock (_liveWriteGate)
        {
            var previous = LiveStoreFor(root);
            if (previous is null || !string.Equals(previous.RootPath, root, StringComparison.OrdinalIgnoreCase)) return;
            if (!Directory.Exists(root)) return;
            var directory = Path.Combine(LiveDirectory, "root-" + Guid.NewGuid().ToString("N"));
            var candidate = new LiveCatalog(directory, root, _liveControl!.LastScanUtc); var adopted = false;
            LiveCatalogStore? next = null;
            try
            {
                var captured = CaptureDiskSnapshot([root], ct, followReparsePoints: true, entrySink: entry =>
                {
                    try { candidate.Add(ToLiveRecord(entry, root, previous.FindRecord(entry.Path)?.Item.OpenCount ?? 0)); }
                    catch (Exception error) { throw new InvalidOperationException("Live kök yenilenemedi.", error); }
                });
                if (captured.UnreadableScopes.Count != 0 || candidate.FindCurrentRecord(root) is null)
                    throw new IOException("Kökün bazı bölümleri okunamıyor; mevcut katalog korunuyor.");
                ct.ThrowIfCancellationRequested();
                next = new LiveCatalogStore(directory, candidate);
                lock (_lock)
                {
                    var replacement = _liveControl!.Roots.Select(binding => binding.Path.Equals(root, StringComparison.OrdinalIgnoreCase)
                        ? new LiveRoot(root, Path.GetFileName(directory)) : binding).ToArray();
                    var remaining = _pendingRepairs.Keys.Where(path => !IsSameOrDescendantPath(path, root)).ToArray();
                    SaveLiveControl(_liveControl with { Roots = replacement, PendingRepairs = remaining.Select(ChangeFeedDeliveryContract.EncodeScope).ToArray(),
                        RootRepairs = _liveRootRepairs.Where(path => !path.Equals(root, StringComparison.OrdinalIgnoreCase)).ToArray() });
                    _liveStores[_liveStores.IndexOf(previous)] = next; next = null; adopted = true;
                    foreach (var path in _pendingRepairs.Keys.Where(path => IsSameOrDescendantPath(path, root)).ToArray()) _pendingRepairs.Remove(path);
                    _liveRootRepairs.Remove(root); PublishLiveState(); previous.Dispose();
                }
                NotifyError("USN kayıt kaybı onarıldı: " + root);
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { NotifyError("USN kök onarımı bekliyor: " + error.Message); }
            finally { next?.Dispose(); if (!adopted) candidate.Dispose(); }
        }
    }, ct);
}
