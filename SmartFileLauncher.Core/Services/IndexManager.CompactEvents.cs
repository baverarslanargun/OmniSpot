using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager
{
    private bool TryHandleCompactFileChange(FileChangeEvent evt)
    {
        string? error = null;
        var applied = false;
        lock (_lock)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var state = (IIndexCatalogSnapshot)CurrentSearchState;
                var path = NormalizeIndexedPath(evt.FullPath);
                state.TryGetItem(path, out var existing);
                if (evt.ChangeType == FileChangeType.Deleted)
                    applied = DeleteCompactEventPath(state, existing?.FullPath ?? path, evt.IsDirectory);
                else if (ShouldSkipReparsePath(path))
                    applied = DeleteCompactSkippedTarget(state, evt, existing);
                else if (evt.ChangeType == FileChangeType.Modified)
                    applied = ModifyCompactEventPath(state, existing, path);
                else
                    applied = AddCompactEventPath(state, evt, path, existing);
            }
            catch (Exception ex)
            {
                error = $"Error handling {evt.ChangeType}: {ex.Message}";
            }
        }
        if (error is not null) NotifyError(error);
        else if (applied) QueueNotification(() => OnFileChange?.Invoke(evt));
        return applied;
    }

    private bool DeleteCompactEventPath(IIndexCatalogSnapshot state, string path, bool fallbackDirectory)
    {
        state.TryGetItem(path, out var item);
        var isDirectory = ResolveCompactDirectory(path, item, fallbackDirectory);
        var next = state.WithoutRecordPathAndDescendants(path);
        using var transaction = _db.BeginTransaction();
        DeletePersistedPath(path, isDirectory);
        transaction.Commit();
        Volatile.Write(ref _publishedSearchState, next);
        Interlocked.Add(ref _subtreeNodesInspected, state.ItemCount - next.ItemCount);
        return true;
    }

    private bool ResolveCompactDirectory(string path, SearchItem? item, bool fallback)
    {
        if (item is not null) return item.IsDirectory;
        if (_db.GetDirectoryByPath(path) is not null) return true;
        if (_db.GetFileByPath(path) is not null) return false;
        return fallback;
    }

    private bool DeleteCompactSkippedTarget(IIndexCatalogSnapshot state, FileChangeEvent evt, SearchItem? existing)
    {
        if (evt.ChangeType != FileChangeType.Renamed || evt.OldPath is null)
            return existing is null || DeleteCompactEventPath(state, existing.FullPath, existing.IsDirectory);
        var oldPath = NormalizeIndexedPath(evt.OldPath);
        if (state.TryGetItem(oldPath, out var oldItem)) oldPath = oldItem.FullPath;
        var oldIsDirectory = ResolveCompactDirectory(oldPath, oldItem, evt.IsDirectory);
        var next = state.WithoutRecordPathAndDescendants(oldPath);
        if (existing is not null) next = next.WithoutRecordPathAndDescendants(existing.FullPath);
        using var transaction = _db.BeginTransaction();
        DeletePersistedPath(oldPath, oldIsDirectory);
        if (existing is not null) DeletePersistedPath(existing.FullPath, existing.IsDirectory);
        transaction.Commit();
        Volatile.Write(ref _publishedSearchState, next);
        Interlocked.Add(ref _subtreeNodesInspected, state.ItemCount - next.ItemCount);
        return true;
    }

    private bool ModifyCompactEventPath(IIndexCatalogSnapshot state, SearchItem? item, string path)
    {
        if (item is null) return !File.Exists(path) && !Directory.Exists(path);
        if (item.IsDirectory) return true;
        var persisted = _db.GetFileByPath(item.FullPath);
        if (persisted is null) return false;
        var info = new FileInfo(path);
        var size = info.Length;
        var modified = info.LastWriteTime;
        var next = state.WithRecordUpserts([item with { SizeBytes = size, LastWriteTime = modified }], _tokenizer);
        using var transaction = _db.BeginTransaction();
        persisted.SizeBytes = size;
        persisted.LastWriteTimeUtc = info.LastWriteTimeUtc.Ticks;
        persisted.LastIndexedTimeUtc = DateTime.UtcNow.Ticks;
        _db.InsertFile(persisted);
        transaction.Commit();
        Volatile.Write(ref _publishedSearchState, next);
        Interlocked.Increment(ref _subtreeNodesInspected);
        return true;
    }

    private bool AddCompactEventPath(IIndexCatalogSnapshot state, FileChangeEvent evt,
        string path, SearchItem? existing)
    {
        var renamed = evt.ChangeType == FileChangeType.Renamed && evt.OldPath is not null;
        var oldPath = renamed ? NormalizeIndexedPath(evt.OldPath!) : null;
        SearchItem? oldItem = null;
        if (oldPath is not null && state.TryGetItem(oldPath, out var found))
        {
            oldItem = found;
            oldPath = found.FullPath;
        }
        if (renamed && _activeRootPaths.Count > 0 &&
            !_activeRootPaths.Any(root => IsSameOrDescendantPath(path, root)))
            return DeleteCompactEventPath(state, oldPath!, oldItem?.IsDirectory ?? evt.IsDirectory);
        if (renamed && !File.Exists(path) && !Directory.Exists(path))
            return DeleteCompactEventPath(state, oldPath!, oldItem?.IsDirectory ?? evt.IsDirectory);

        var isDirectory = Directory.Exists(path) || (!File.Exists(path) && evt.IsDirectory);
        var samePath = oldPath is not null && string.Equals(oldPath, path, StringComparison.OrdinalIgnoreCase);
        if (existing is not null && existing.IsDirectory == isDirectory && (!renamed || !samePath))
        {
            var persisted = existing.IsDirectory
                ? _db.GetDirectoryByPath(existing.FullPath) is not null
                : _db.GetFileByPath(existing.FullPath) is not null;
            if (!persisted) return false;
            return !renamed || DeleteCompactEventPath(state, oldPath!, oldItem?.IsDirectory ?? evt.IsDirectory);
        }

        var parentPath = Path.GetDirectoryName(path);
        if (parentPath is null ||
            (!state.TryGetItem(parentPath, out var parent) &&
             !string.Equals(parentPath, _compactSingleRootPath, StringComparison.OrdinalIgnoreCase))) return false;
        if (parent is not null && !parent.IsDirectory) return false;
        var persistedParent = _db.GetDirectoryByPath(parent?.FullPath ?? parentPath);
        if (persistedParent is null) return false;
        if (!Directory.Exists(path) && !File.Exists(path)) return true;

        var snapshot = CaptureDiskSnapshot([path], CancellationToken.None, followReparsePoints: true);
        if (snapshot.UnreadableScopes.Count != 0 || !snapshot.Entries.ContainsKey(path)) return false;
        if (snapshot.ProtectedScopes.Any(scope => !snapshot.Entries.ContainsKey(scope))) return false;

        var next = state;
        var preserveIdentity = renamed && samePath && oldItem is not null;
        using var transaction = _db.BeginTransaction();
        if (oldPath is not null && !preserveIdentity)
        {
            next = next.WithoutRecordPathAndDescendants(oldPath);
            var oldDirectory = ResolveCompactDirectory(oldPath, oldItem, evt.IsDirectory);
            DeletePersistedPath(oldPath, oldDirectory);
        }
        if (existing is not null && (oldPath is null || !samePath))
        {
            next = next.WithoutRecordPathAndDescendants(existing.FullPath);
            DeletePersistedPath(existing.FullPath, existing.IsDirectory);
        }

        var upserts = new List<SearchItem>(snapshot.Entries.Count);
        var parents = new Dictionary<string, IndexedDirectory>(StringComparer.OrdinalIgnoreCase)
        {
            [persistedParent.FullPath] = persistedParent
        };
        foreach (var entry in snapshot.Entries.Values.OrderBy(x => x.Path.Length))
        {
            var entryParentPath = Path.GetDirectoryName(entry.Path);
            if (entryParentPath is null || !parents.TryGetValue(entryParentPath, out var entryParent))
                throw new InvalidOperationException($"Cannot index path because its parent is missing: {entry.Path}");
            if (entry.IsDirectory)
            {
                var info = new DirectoryInfo(entry.Path);
                if (preserveIdentity && state.TryGetItem(entry.Path, out var previousDirectory))
                {
                    var persistedDirectory = _db.GetDirectoryByPath(previousDirectory.FullPath)
                        ?? throw new InvalidOperationException($"Directory is missing from the database: {previousDirectory.FullPath}");
                    _db.UpdateDirectoryIdentity(persistedDirectory.Id, entry.Path, info.Name);
                }
                var row = new IndexedDirectory
                {
                    FullPath = entry.Path, Name = info.Name, ParentId = entryParent.Id,
                    Depth = entryParent.Depth + 1, LastWriteTimeUtc = entry.LastWriteTimeUtc,
                    LastIndexedTimeUtc = DateTime.UtcNow.Ticks,
                    IsHidden = (info.Attributes & FileAttributes.Hidden) != 0
                };
                row.Id = _db.InsertDirectory(row);
                parents[row.FullPath] = row;
                upserts.Add(new(row.Name, row.FullPath, true, null, null, null, 0, entryParent.FullPath));
            }
            else
            {
                var info = new FileInfo(entry.Path);
                IndexedFile? persistedFile = null;
                if (preserveIdentity && state.TryGetItem(entry.Path, out var previousFile))
                {
                    persistedFile = _db.GetFileByPath(previousFile.FullPath)
                        ?? throw new InvalidOperationException($"File is missing from the database: {previousFile.FullPath}");
                    _db.UpdateFileIdentity(persistedFile.Id, entry.Path, info.Name, info.Extension.ToLowerInvariant());
                }
                var row = new IndexedFile
                {
                    FullPath = entry.Path, FileName = info.Name, Extension = info.Extension.ToLowerInvariant(),
                    DirectoryId = entryParent.Id, SizeBytes = info.Length,
                    CreatedTimeUtc = persistedFile?.CreatedTimeUtc ?? info.CreationTimeUtc.Ticks,
                    LastWriteTimeUtc = info.LastWriteTimeUtc.Ticks,
                    OpenCount = persistedFile?.OpenCount ?? 0,
                    LastIndexedTimeUtc = DateTime.UtcNow.Ticks,
                    IsHidden = (info.Attributes & FileAttributes.Hidden) != 0,
                    IsSystem = (info.Attributes & FileAttributes.System) != 0
                };
                _db.InsertFile(row);
                upserts.Add(new(row.FileName, row.FullPath, false, row.SizeBytes,
                    row.CreatedTime, row.LastWriteTime, row.OpenCount, entryParent.FullPath));
            }
        }
        next = next.WithRecordUpserts(upserts, _tokenizer);
        transaction.Commit();
        Volatile.Write(ref _publishedSearchState, next);
        Interlocked.Add(ref _subtreeNodesInspected, snapshot.Entries.Count);
        return true;
    }

    private void IncrementCompactOpenCount(string path)
    {
        lock (_lock)
        {
            var state = (IIndexCatalogSnapshot)CurrentSearchState;
            var normalized = NormalizeIndexedPath(path);
            if (!state.TryGetItem(normalized, out var item) || item.IsDirectory) return;
            using var transaction = _db.BeginTransaction();
            var count = _db.TryIncrementOpenCount(item.FullPath);
            if (count is null) throw new InvalidOperationException($"File is missing from the database: {item.FullPath}");
            var next = state.WithRecordUpserts([item with { OpenCount = count.Value }], _tokenizer);
            transaction.Commit();
            Volatile.Write(ref _publishedSearchState, next);
        }
    }
}
