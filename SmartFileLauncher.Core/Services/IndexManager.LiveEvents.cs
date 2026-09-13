using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager
{
    private sealed class LiveMetadataException(string message, Exception? inner = null) : IOException(message, inner);
    internal Task CommitContinuousDeliveryAsync(IReadOnlyList<ChangeFeedRootPageDto> pages, string deliveryId, CancellationToken ct)
        => Task.Run(() =>
        {
            var repairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var changes = new List<FileChangeEvent>();
            foreach (var page in pages)
            {
                if (!_activeRootPaths.Any(root => IsSameOrDescendantPath(page.RootPath, root))) throw new InvalidDataException("USN teslim kökü indeks dışında.");
                changes.AddRange(page.Events.Select(change => new FileChangeEvent { FullPath = change.Path, OldPath = change.OldPath, IsDirectory = change.IsDirectory,
                    ChangeType = change.Kind switch { ChangeFeedEventKind.Created => FileChangeType.Created, ChangeFeedEventKind.Deleted => FileChangeType.Deleted,
                        ChangeFeedEventKind.Renamed => FileChangeType.Renamed, _ => FileChangeType.Modified }, Timestamp = DateTime.UtcNow }));
                if (page.AuthorizationScopesUtf16 is not null)
                    foreach (var scope in page.AuthorizationScopesUtf16) repairs.Add(ValidateRepairPath(ChangeFeedDeliveryContract.DecodeScope(scope)));
                if (page.ProducerGap is ChangeFeedGapReason.JournalIdChanged or ChangeFeedGapReason.CursorOutsideJournal or
                    ChangeFeedGapReason.FeedStateInvalid or ChangeFeedGapReason.DeliveryQueueOverflow or ChangeFeedGapReason.NotYetSynchronized or
                    ChangeFeedGapReason.EntryTooLarge or ChangeFeedGapReason.RootIdentityChanged)
                {
                    if (page.ProducerGap == ChangeFeedGapReason.RootIdentityChanged) Interlocked.Exchange(ref _liveResubscribeNeeded, 1);
                    lock (_lock)
                    {
                        _liveRootRepairs.Add(page.RootPath);
                        SaveLiveControl(_liveControl! with { RootRepairs = _liveRootRepairs.ToArray() });
                    }
                    repairs.Add(page.RootPath);
                }
                if (page.PayloadTooLarge ||
                    page.AuthorizationGap && page.AuthorizationScopesUtf16 is not { Count: > 0 }) repairs.Add(page.RootPath);
            }
            ApplyLiveChanges(changes, deliveryId, repairs, ct);
        }, ct);

    private void ApplyLiveChanges(IReadOnlyList<FileChangeEvent> changes, string? deliveryId, HashSet<string> repairs, CancellationToken ct)
    {
        changes = changes.Where(change => !IsLiveStoragePath(change.FullPath) ||
            change.OldPath is not null && !IsLiveStoragePath(change.OldPath)).ToArray();
        repairs.RemoveWhere(IsLiveStoragePath);
        if (changes.Count == 0 && repairs.Count == 0) return;
        var mutations = CoalesceLiveModifications(changes);
        lock (_liveWriteGate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (!_isInitialized) throw new InvalidOperationException("Live katalog hazır değil.");
            foreach (var store in _liveStores)
            {
                var relevant = mutations.Where(change => IsSameOrDescendantPath(change.FullPath, store.RootPath) ||
                    change.OldPath is not null && IsSameOrDescendantPath(change.OldPath, store.RootPath)).ToArray();
                if (relevant.Length == 0 && !repairs.Any(path => IsSameOrDescendantPath(path, store.RootPath))) continue;
                store.Commit(candidate =>
                {
                    foreach (var change in relevant)
                    {
                        ct.ThrowIfCancellationRequested();
                        try { ApplyLiveEvent(candidate, change, ct, repairs); }
                        catch (LiveMetadataException)
                        {
                            if (IsSameOrDescendantPath(change.FullPath, store.RootPath)) repairs.Add(change.FullPath);
                            if (change.OldPath is not null && IsSameOrDescendantPath(change.OldPath, store.RootPath)) repairs.Add(change.OldPath);
                        }
                    }
                }, deliveryId, ct, pendingRepairs: () => repairs.Where(path => IsSameOrDescendantPath(path, store.RootPath)).Select(ChangeFeedDeliveryContract.EncodeScope).ToArray());
            }
            if (repairs.Count > 0 && !QueueLiveRepairs(repairs.ToArray())) throw new IOException("USN yerel onarımları kalıcılaştırılamadı.");
            PublishLiveState();
        }
        foreach (var change in changes) QueueNotification(() => OnFileChange?.Invoke(change));
    }

    internal static IReadOnlyList<FileChangeEvent> CoalesceLiveModifications(IReadOnlyList<FileChangeEvent> changes)
    {
        var result = new List<FileChangeEvent>(changes.Count);
        var pending = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        foreach (var change in changes)
        {
            if (change.ChangeType != FileChangeType.Modified || change.IsDirectory || change.OldPath is not null)
            {
                pending.Clear(); result.Add(change); continue;
            }
            if (pending.TryGetValue(change.FullPath, out var index)) result[index] = change;
            else { pending.Add(change.FullPath, result.Count); result.Add(change); }
        }
        return result;
    }

    private void ApplyLiveEvent(LiveCatalog candidate, FileChangeEvent change, CancellationToken ct, HashSet<string> repairs)
    {
        var path = NormalizeIndexedPath(change.FullPath); var inside = IsSameOrDescendantPath(path, candidate.RootPath);
        var oldPath = change.OldPath is null ? null : NormalizeIndexedPath(change.OldPath);
        var oldInside = oldPath is not null && IsSameOrDescendantPath(oldPath, candidate.RootPath);
        PackedRecord? record = inside ? ReadLiveFile(path, candidate.RootPath, candidate.FindCurrentRecord(path)?.Item.OpenCount ?? 0) : null;
        if (change.ChangeType == FileChangeType.Renamed && oldInside && oldPath is not null && !string.Equals(path, oldPath, StringComparison.OrdinalIgnoreCase))
        {
            var oldCurrent = ReadLiveFile(oldPath, candidate.RootPath, 0);
            var previous = candidate.FindCurrentRecord(oldPath);
            if (oldCurrent is null && record is not null && previous is not null && previous.Item.IsDirectory == record.Item.IsDirectory)
            {
                EnsureLiveParents(candidate, path, ct);
                record = record with { Item = record.Item with { OpenCount = previous.Item.OpenCount } };
                candidate.ApplyMutation(new(path, record, oldPath)); return;
            }
            if (oldCurrent is null) candidate.ApplyMutation(new(oldPath, null));
            else { EnsureLiveParents(candidate, oldPath, ct); candidate.ApplyMutation(new(oldPath, oldCurrent)); }
        }
        if (!inside) return;
        if (record is null) { candidate.ApplyMutation(new(path, null)); return; }
        var existing = candidate.FindCurrentRecord(path);
        EnsureLiveParents(candidate, path, ct);
        if (record.Item.IsDirectory && existing is null)
        {
            CaptureLiveScope(candidate, path, ct, repairs);
            return;
        }
        candidate.ApplyMutation(new(path, record));
    }

    private PackedRecord? ReadLiveFile(string path, string root, int openCount)
    {
        if (IsLiveStoragePath(path)) return null;
        try
        {
            var attributes = File.GetAttributes(path);
            if (ShouldSkipReparsePath(path) || IsCompactEventExcluded(path)) return null;
            if ((attributes & FileAttributes.Directory) != 0)
            {
                if ((attributes & FileAttributes.ReparsePoint) != 0) TrackLiveLink(path, dispatchPaused: false);
                var info = new DirectoryInfo(path);
                return ToLiveRecord(new(path, true, info.LastWriteTimeUtc.Ticks, 0, attributes), root);
            }
            var file = new FileInfo(path);
            return ToLiveRecord(new(path, false, file.LastWriteTimeUtc.Ticks, file.Length, attributes, file.CreationTimeUtc.Ticks), root, openCount);
        }
        catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return null; }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException) { throw new LiveMetadataException("Dosya metası okunamadı: " + path, error); }
    }
    private void EnsureLiveParents(LiveCatalog candidate, string path, CancellationToken ct)
    {
        var pending = new Stack<string>();
        for (var parent = Path.GetDirectoryName(path); parent is not null && IsSameOrDescendantPath(parent, candidate.RootPath); parent = Path.GetDirectoryName(parent))
        {
            if (candidate.FindCurrentRecord(parent) is not null) break;
            pending.Push(parent);
        }
        while (pending.TryPop(out var parent))
        {
            ct.ThrowIfCancellationRequested();
            var record = ReadLiveFile(parent, candidate.RootPath, 0);
            if (record is not { Item.IsDirectory: true }) throw new LiveMetadataException("Live ebeveyn okunamadı: " + parent);
            candidate.ApplyMutation(new(parent, record));
        }
    }
    private void CaptureLiveScope(LiveCatalog candidate, string path, CancellationToken ct, HashSet<string> repairs)
    {
        var snapshot = CaptureDiskSnapshot([path], ct, followReparsePoints: true, entrySink: entry =>
        {
            try
            {
                if (entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0) TrackLiveLink(entry.Path, dispatchPaused: false);
                var previous = candidate.FindCurrentRecord(entry.Path);
                candidate.ApplyMutation(new(entry.Path, ToLiveRecord(entry, candidate.RootPath, previous?.Item.OpenCount ?? 0)));
            }
            catch (Exception error) { throw new InvalidOperationException("Live kapsamı kaydedilemedi.", error); }
        });
        foreach (var unreadable in snapshot.UnreadableScopes) repairs.Add(unreadable);
    }

    private bool QueueLiveRepairs(IReadOnlyList<string> paths)
    {
        lock (_lock)
        {
            var normalized = paths.Select(ValidateRepairPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            var combined = _pendingRepairs.Keys.Concat(normalized).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
            if (combined.Length > MaximumPendingRepairs) return false;
            var encoded = combined.Select(ChangeFeedDeliveryContract.EncodeScope).ToArray();
            if (encoded.Sum(path => path.Length) > MaximumPendingRepairBytes) return false;
            SaveLiveControl(_liveControl! with { PendingRepairs = encoded });
            foreach (var path in normalized) _pendingRepairs[path] = ++_repairVersion;
            if (normalized.Any(path => _liveRootRepairs.Contains(path)))
                NotifyError("USN günlüğünde gerçek kayıt kaybı var; etkilenen kökün kataloğu arka planda yenilenecek.");
            else if (normalized.Any(path => _activeRootPaths.Contains(path, StringComparer.OrdinalIgnoreCase)))
                NotifyError("USN erişim sorunu bekleniyor; katalog açık tutuluyor, otomatik tam tarama yapılmıyor.");
            return true;
        }
    }
    internal async Task RetryLiveRepairsAsync(CancellationToken ct)
    {
        lock (_lock)
            foreach (var scope in _liveStores.SelectMany(store => store.PendingRepairs).Select(ChangeFeedDeliveryContract.DecodeScope))
                if (!_pendingRepairs.ContainsKey(scope)) _pendingRepairs[scope] = ++_repairVersion;
        if (LiveWatcherRoots.Length > 0 && !_watcher.IsWatching) SetupWatchers(LiveWatcherRoots);
        string[] paths;
        string[] roots;
        lock (_lock) roots = _liveRootRepairs.ToArray();
        foreach (var root in roots) await RebuildLiveRootAsync(root, ct).ConfigureAwait(false);
        lock (_lock) paths = _pendingRepairs.Keys.Where(path => !_activeRootPaths.Contains(path, StringComparer.OrdinalIgnoreCase) ||
            LiveWatcherRoots.Contains(path, StringComparer.OrdinalIgnoreCase)).Take(16).ToArray();
        foreach (var path in paths) await ReconcileLiveScopeAsync(path, ct).ConfigureAwait(false);
    }
    private Task<bool> ReconcileLiveScopeAsync(string path, CancellationToken ct) => Task.Run(() =>
    {
        lock (_liveWriteGate)
        {
            var store = LiveStoreFor(path); if (store is null) return false;
            var failures = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            try
            {
                store.Commit(candidate =>
                {
                    var current = ReadLiveFile(path, store.RootPath, candidate.FindCurrentRecord(path)?.Item.OpenCount ?? 0);
                    if (current is null) { candidate.ApplyMutation(new(path, null)); return; }
                    EnsureLiveParents(candidate, path, ct);
                    if (!current.Item.IsDirectory) { candidate.ApplyMutation(new(path, current)); return; }
                    var previous = ((IQueryCatalogSnapshot)store.State).GetDescendants(current.Item, ct);
                    CaptureLiveScope(candidate, path, ct, failures);
                    foreach (var old in previous)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (failures.Any(scope => IsSameOrDescendantPath(old.FullPath, scope))) continue;
                        if (ReadLiveFile(old.FullPath, store.RootPath, old.OpenCount) is null) candidate.ApplyMutation(new(old.FullPath, null));
                    }
                }, ct: ct);
                PublishLiveState();
                if (failures.Count > 0) { QueueLiveRepairs(failures.ToArray()); return false; }
                var completed = store.PendingRepairs.Where(encoded => IsSameOrDescendantPath(ChangeFeedDeliveryContract.DecodeScope(encoded), path)).ToArray();
                if (completed.Length > 0) store.Commit(_ => { }, ct: ct, removeRepairs: completed);
                lock (_lock)
                {
                    _pendingRepairs.Remove(path);
                    SaveLiveControl(_liveControl! with { PendingRepairs = _pendingRepairs.Keys.Select(ChangeFeedDeliveryContract.EncodeScope).ToArray() });
                }
                return true;
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { NotifyError("Yerel katalog onarımı bekliyor: " + error.Message); return false; }
        }
    }, ct);
}
