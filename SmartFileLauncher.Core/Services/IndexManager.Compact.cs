using System.Diagnostics;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager
{
    private sealed record CompactPreparedEntry(
        SearchItem Item,
        long LastWriteTimeUtc,
        long CreatedTimeUtc,
        bool IsHidden,
        bool IsSystem);

    private IIndexCatalogSnapshot CurrentCompactSnapshot =>
        (IIndexCatalogSnapshot)CurrentSearchState;

    private async Task<bool> LoadCompactFromCacheMultiAsync(
        List<string> rootPaths,
        CancellationToken ct)
    {
        var started = Stopwatch.StartNew();
        var accepted = await Task.Run(() =>
        {
            lock (_lock)
            {
                var directories = _db.GetAllDirectories().ToArray();
                if (directories.Any(directory => !IsCanonicalIndexedPath(directory.FullPath)))
                {
                    return false;
                }

                var existingRoots = rootPaths
                    .Where(Directory.Exists)
                    .Distinct(StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(path => path, PathName, StringComparer.OrdinalIgnoreCase);
                var directoriesById = directories.ToDictionary(directory => directory.Id);
                var directoryPaths = directories
                    .ToDictionary(directory => directory.FullPath, StringComparer.OrdinalIgnoreCase);
                var items = new Dictionary<string, SearchItem>(StringComparer.OrdinalIgnoreCase);

                foreach (var (rootPath, rootName) in existingRoots)
                {
                    items[rootPath] = CompactDirectoryItem(rootName, rootPath, parentPath: "");
                }

                var detached = 0;
                foreach (var directory in directories)
                {
                    ct.ThrowIfCancellationRequested();
                    if (existingRoots.ContainsKey(directory.FullPath))
                        continue;

                    string? parentPath;
                    if (directory.ParentId is long parentId &&
                        directoriesById.TryGetValue(parentId, out var parent))
                    {
                        parentPath = parent.FullPath;
                    }
                    else
                    {
                        parentPath = existingRoots.Keys.FirstOrDefault(root =>
                            directory.FullPath.StartsWith(root, StringComparison.OrdinalIgnoreCase)) ?? "";
                        detached++;
                    }

                    items[directory.FullPath] = CompactDirectoryItem(
                        directory.Name,
                        directory.FullPath,
                        parentPath);
                }

                var loadedFiles = 0;
                var totalFiles = _db.GetFileCount();
                foreach (var file in _db.GetAllFiles())
                {
                    ct.ThrowIfCancellationRequested();
                    if (!IsCanonicalIndexedPath(file.FullPath)) return false;
                    string? parentPath = null;
                    if (file.DirectoryId > 0 &&
                        directoriesById.TryGetValue(file.DirectoryId, out var parentDirectory))
                    {
                        parentPath = parentDirectory.FullPath;
                    }
                    else
                    {
                        var physicalParent = Path.GetDirectoryName(file.FullPath);
                        if (physicalParent is not null && directoryPaths.ContainsKey(physicalParent))
                        {
                            parentPath = physicalParent;
                        }
                        else if (physicalParent is not null)
                        {
                            parentPath = existingRoots.Keys.FirstOrDefault(root =>
                                string.Equals(physicalParent, root, StringComparison.OrdinalIgnoreCase));
                        }

                        if (parentPath is null)
                            detached++;
                    }

                    items[file.FullPath] = new SearchItem(
                        file.FileName,
                        file.FullPath,
                        IsDirectory: false,
                        file.SizeBytes,
                        file.CreatedTime,
                        file.LastWriteTime,
                        file.OpenCount,
                        parentPath);

                    loadedFiles++;
                    if (loadedFiles % 100 == 0)
                    {
                        var percentage = totalFiles == 0
                            ? 100
                            : (int)(loadedFiles * 100.0 / totalFiles);
                        ReportProgress(
                            $"Önbellek yükleniyor: {loadedFiles}/{totalFiles}",
                            percentage,
                            loadedFiles,
                            0);
                    }
                }

                ReportProgress("Arama kataloğu hazırlanıyor...", 100, loadedFiles, 0, isIndeterminate: true, isCatalogBuild: true);
                var candidate = CompactSearchState.Create(items.Values, _tokenizer);
                _pathToNode.Clear();
                _rootNode = null;
                _detachedNodeCount = detached;
                _compactRootAvailable = true;
                _compactSingleRootPath = null;
                PublishCompactSnapshot(candidate, incrementalReconciliation: false);
                return true;
            }
        }, ct).ConfigureAwait(false);

        if (!accepted)
            return false;

        started.Stop();
        ReportProgress("Önbellek yüklendi", 100, IndexedFileCount, started.ElapsedMilliseconds);
        return true;
    }

    private Task BootstrapCompactScanAsync(string rootPath, CancellationToken ct) =>
        BootstrapCompactScanCoreAsync(
            new List<string> { rootPath },
            singleRootPath: rootPath,
            ct);

    private Task BootstrapCompactScanMultiAsync(List<string> rootPaths, CancellationToken ct) =>
        BootstrapCompactScanCoreAsync(rootPaths, singleRootPath: null, ct);

    private async Task BootstrapCompactScanCoreAsync(
        List<string> rootPaths,
        string? singleRootPath,
        CancellationToken ct)
    {
        foreach (var rootPath in rootPaths)
            EnsureMeasurementDirectorySafe(rootPath);

        var started = Stopwatch.StartNew();
        await Task.Run(() =>
        {
            lock (_lock)
            {
                foreach (var rootPath in rootPaths)
                    EnsureMeasurementDirectorySafe(rootPath);

                var snapshot = CaptureDiskSnapshot(
                    rootPaths,
                    ct,
                    followReparsePoints: true);
                var prepared = PrepareCompactEntries(snapshot, rootPaths, ct);
                var searchItems = singleRootPath is null
                    ? prepared.Select(entry => entry.Item)
                    : prepared
                        .Where(entry => !string.Equals(
                            entry.Item.FullPath,
                            singleRootPath,
                            StringComparison.OrdinalIgnoreCase))
                        .Select(entry => entry.Item);
                var candidate = CompactSearchState.Create(searchItems, _tokenizer);

                using var transaction = _db.BeginTransaction();
                try
                {
                    _db.ClearIndex();
                    PersistCompactEntries(prepared, ct);
                    var rootsKey = string.Join(
                        "|",
                        rootPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
                    _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, rootsKey);
                    _db.SetMetadata(IndexMetadata.Keys.LastFullScanTime, DateTime.UtcNow.Ticks.ToString());
                    _db.SetMetadata(IndexMetadata.Keys.TotalFilesIndexed, prepared.Count.ToString());
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }

                _pathToNode.Clear();
                _rootNode = null;
                _detachedNodeCount = 0;
                _compactRootAvailable = true;
                _compactSingleRootPath = singleRootPath;
                PublishCompactSnapshot(candidate, incrementalReconciliation: false);
            }
        }, ct).ConfigureAwait(false);

        started.Stop();
        ReportProgress("Tarama tamamlandı", 100, IndexedFileCount, started.ElapsedMilliseconds);
    }

    private async Task RescanCompactAsync(string rootPath, CancellationToken ct)
    {
        await _lifecycleGate.WaitAsync(ct).ConfigureAwait(false);
        var wasInitialized = _isInitialized;
        var previousRoots = _activeRootPaths.ToList();
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopBackgroundSyncAsync().ConfigureAwait(false);
            _watcher.Stop();
            _watcher.ClearWatches();

            try
            {
                var normalizedRootPath = NormalizeIndexedPath(rootPath);
                await BootstrapCompactScanAsync(normalizedRootPath, ct).ConfigureAwait(false);
                var paths = new List<string> { normalizedRootPath };
                _activeRootPaths = paths;
                _isInitialized = true;
                SetupWatchers(paths);
                StartBackgroundReconciliation(paths);
            }
            catch
            {
                if (wasInitialized)
                {
                    _activeRootPaths = previousRoots;
                    _isInitialized = true;
                    SetupWatchers(previousRoots);
                    StartBackgroundReconciliation(previousRoots);
                }
                throw;
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private void ResetCompactInMemoryIndex()
    {
        _pathToNode.Clear();
        _rootNode = null;
        _detachedNodeCount = 0;
        _compactRootAvailable = false;
        _compactSingleRootPath = null;
        Volatile.Write(ref _publishedSearchState, ISearchStateReader.Empty(_layout));
    }

    private int ApplyCompactReconciliationSnapshot(
        IReadOnlyList<string> rootPaths,
        ReconciliationSnapshot suppliedSnapshot,
        CancellationToken ct)
    {
        _ = suppliedSnapshot;
        lock (_lock)
        {
            var snapshot = CaptureDiskSnapshot(rootPaths, ct);
            MergeCompactSnapshotStatus(snapshot, suppliedSnapshot);
            var current = CurrentCompactSnapshot;
            var directories = _db.GetAllDirectories().ToArray();
            var files = _db.GetAllFiles().ToArray();
            var directoriesByPath = directories.ToDictionary(
                directory => directory.FullPath,
                StringComparer.OrdinalIgnoreCase);
            var filesByPath = files.ToDictionary(
                file => file.FullPath,
                StringComparer.OrdinalIgnoreCase);

            var removed = FindCompactReconciliationRemovals(
                current,
                rootPaths,
                snapshot,
                ct);
            var prepared = PrepareCompactEntries(
                snapshot,
                _activeRootPaths.Count == 0 ? rootPaths : _activeRootPaths,
                ct,
                filesByPath);
            var upserts = new List<SearchItem>();
            var persistedUpserts = new List<CompactPreparedEntry>();
            var changes = removed.Count;

            foreach (var entry in prepared)
            {
                ct.ThrowIfCancellationRequested();
                var item = entry.Item;
                var isExcludedSingleRoot = _compactSingleRootPath is not null &&
                    string.Equals(
                        _compactSingleRootPath,
                        item.FullPath,
                        StringComparison.OrdinalIgnoreCase);
                var stateChanged = !isExcludedSingleRoot &&
                    (!current.TryGetItem(item.FullPath, out var oldItem) || oldItem != item);
                bool databaseChanged;
                if (item.IsDirectory)
                {
                    IndexedDirectory? expectedParent = null;
                    var expectedParentKnown = string.IsNullOrEmpty(item.ParentPath) ||
                        directoriesByPath.TryGetValue(item.ParentPath, out expectedParent);
                    long? expectedParentId = string.IsNullOrEmpty(item.ParentPath)
                        ? null
                        : expectedParent?.Id;
                    databaseChanged = !directoriesByPath.TryGetValue(item.FullPath, out var oldDirectory) ||
                                      oldDirectory.LastWriteTimeUtc != entry.LastWriteTimeUtc ||
                                      oldDirectory.FullPath != item.FullPath || oldDirectory.Name != item.Name ||
                                      oldDirectory.IsHidden != entry.IsHidden ||
                                      !expectedParentKnown ||
                                      oldDirectory.ParentId != expectedParentId;
                }
                else
                {
                    IndexedDirectory? expectedParent = null;
                    var expectedParentKnown = item.ParentPath is not null &&
                        directoriesByPath.TryGetValue(item.ParentPath, out expectedParent);
                    databaseChanged = !filesByPath.TryGetValue(item.FullPath, out var oldFile) ||
                                      oldFile.LastWriteTimeUtc != entry.LastWriteTimeUtc ||
                                      oldFile.SizeBytes != item.SizeBytes ||
                                      oldFile.FullPath != item.FullPath || oldFile.FileName != item.Name ||
                                      oldFile.IsHidden != entry.IsHidden || oldFile.IsSystem != entry.IsSystem ||
                                      !expectedParentKnown ||
                                      oldFile.DirectoryId != expectedParent?.Id;
                }

                if (stateChanged)
                    upserts.Add(item);
                if (databaseChanged)
                    persistedUpserts.Add(entry);
                if (stateChanged || databaseChanged)
                    changes++;
            }

            if (changes == 0)
                return 0;

            var candidate = current;
            foreach (var path in removed)
                candidate = candidate.WithoutRecordPathAndDescendants(path);
            var affectedItemCount = current.ItemCount - candidate.ItemCount;
            if (upserts.Count != 0)
                candidate = candidate.WithRecordUpserts(upserts, _tokenizer);
            var publishChangeCount = affectedItemCount + upserts.Count;
            var incrementalPublish = current.ItemCount != 0 &&
                (long)publishChangeCount * 10 < current.ItemCount;
            if (!incrementalPublish)
                candidate = CompactSearchState.Create(candidate.GetAllItems(ct), _tokenizer);
            ValidateCompactPlan(snapshot, prepared, removed, current, ct);

            using var transaction = _db.BeginTransaction();
            try
            {
                DeleteCompactPaths(removed, current);
                PersistCompactEntries(persistedUpserts, ct, filesByPath);
                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }

            PublishCompactSnapshot(candidate, incrementalPublish);
            return changes;
        }
    }

    private List<string> FindCompactReconciliationRemovals(
        IIndexCatalogSnapshot current,
        IReadOnlyList<string> rootPaths,
        ReconciliationSnapshot snapshot,
        CancellationToken ct)
    {
        var candidates = new List<string>();
        foreach (var item in current.GetAllItems()
                     .Where(item => rootPaths.Any(root =>
                         IsSameOrDescendantPath(item.FullPath, root)))
                     .OrderBy(item => item.FullPath.Length))
        {
            ct.ThrowIfCancellationRequested();
            if (rootPaths.Any(root => string.Equals(
                    root,
                    item.FullPath,
                    StringComparison.OrdinalIgnoreCase)))
            {
                continue;
            }

            if (candidates.Any(parent => IsSameOrDescendantPath(item.FullPath, parent)))
                continue;

            var remove = snapshot.Entries.TryGetValue(item.FullPath, out var diskEntry)
                ? diskEntry.IsDirectory != item.IsDirectory
                : snapshot.ExcludedScopes.Any(scope =>
                    IsSameOrDescendantPath(item.FullPath, scope)) ||
                  (!snapshot.ProtectedScopes.Any(scope =>
                       IsSameOrDescendantPath(item.FullPath, scope)) &&
                   (item.IsDirectory
                       ? !Directory.Exists(item.FullPath)
                       : !File.Exists(item.FullPath)));
            if (remove)
                candidates.Add(item.FullPath);
        }

        return candidates;
    }

    private List<CompactPreparedEntry> PrepareCompactEntries(
        ReconciliationSnapshot snapshot,
        IReadOnlyList<string> rootPaths,
        CancellationToken ct,
        IReadOnlyDictionary<string, IndexedFile>? existingFiles = null)
    {
        var roots = rootPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
        var prepared = new List<CompactPreparedEntry>(snapshot.Entries.Count);
        foreach (var entry in snapshot.Entries.Values
                     .OrderBy(entry => entry.IsDirectory ? 0 : 1)
                     .ThenBy(entry => entry.Path.Length))
        {
            ct.ThrowIfCancellationRequested();
            var parentPath = roots.Contains(entry.Path)
                ? ""
                : Path.GetDirectoryName(entry.Path) is { } parent
                    ? NormalizeIndexedPath(parent)
                    : null;
            var attributes = File.GetAttributes(entry.Path);
            if (entry.IsDirectory)
            {
                prepared.Add(new CompactPreparedEntry(
                    CompactDirectoryItem(PathName(entry.Path), entry.Path, parentPath),
                    entry.LastWriteTimeUtc,
                    CreatedTimeUtc: 0,
                    IsHidden: (attributes & FileAttributes.Hidden) != 0,
                    IsSystem: (attributes & FileAttributes.System) != 0));
                continue;
            }

            var fileInfo = new FileInfo(entry.Path);
            IndexedFile? persisted = null;
            if (existingFiles is not null)
                existingFiles.TryGetValue(entry.Path, out persisted);
            var createdTimeUtc = persisted?.CreatedTimeUtc ?? fileInfo.CreationTimeUtc.Ticks;
            var openCount = persisted?.OpenCount ?? 0;
            prepared.Add(new CompactPreparedEntry(
                new SearchItem(
                    fileInfo.Name,
                    entry.Path,
                    IsDirectory: false,
                    entry.SizeBytes,
                    new DateTime(createdTimeUtc, DateTimeKind.Utc).ToLocalTime(),
                    new DateTime(entry.LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime(),
                    openCount,
                    parentPath),
                entry.LastWriteTimeUtc,
                createdTimeUtc,
                IsHidden: (attributes & FileAttributes.Hidden) != 0,
                IsSystem: (attributes & FileAttributes.System) != 0));
        }

        return prepared;
    }

    private static void MergeCompactSnapshotStatus(
        ReconciliationSnapshot source,
        ReconciliationSnapshot destination)
    {
        destination.ProtectedScopes.UnionWith(source.ProtectedScopes);
        destination.UnreadableScopes.UnionWith(source.UnreadableScopes);
        destination.ExcludedScopes.UnionWith(source.ExcludedScopes);
        foreach (var error in source.Errors)
        {
            if (!destination.Errors.Contains(error, StringComparer.Ordinal))
                destination.Errors.Add(error);
        }
    }

    private void PersistCompactEntries(
        IReadOnlyCollection<CompactPreparedEntry> entries,
        CancellationToken ct,
        IReadOnlyDictionary<string, IndexedFile>? existingFiles = null)
    {
        var now = DateTime.UtcNow.Ticks;
        var directoryIds = _db.GetAllDirectories().ToDictionary(
            directory => directory.FullPath,
            directory => (directory.Id, directory.Depth),
            StringComparer.OrdinalIgnoreCase);

        foreach (var entry in entries
                     .Where(entry => entry.Item.IsDirectory)
                     .OrderBy(entry => entry.Item.FullPath.Length))
        {
            ct.ThrowIfCancellationRequested();
            var parentPath = entry.Item.ParentPath;
            long? parentId = null;
            var depth = 0;
            if (!string.IsNullOrEmpty(parentPath))
            {
                if (!directoryIds.TryGetValue(parentPath, out var parent))
                    throw new InvalidOperationException(
                        $"Cannot persist directory because its parent is missing: {entry.Item.FullPath}");
                parentId = parent.Id;
                depth = parent.Depth + 1;
            }

            if (directoryIds.TryGetValue(entry.Item.FullPath, out var existingDirectory))
                _db.UpdateDirectoryIdentity(existingDirectory.Id, entry.Item.FullPath, entry.Item.Name);
            var id = _db.InsertDirectory(new IndexedDirectory
            {
                FullPath = entry.Item.FullPath,
                Name = entry.Item.Name,
                ParentId = parentId,
                Depth = depth,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                LastIndexedTimeUtc = now,
                IsHidden = entry.IsHidden
            });
            directoryIds[entry.Item.FullPath] = (id, depth);
        }

        foreach (var entry in entries.Where(entry => !entry.Item.IsDirectory))
        {
            ct.ThrowIfCancellationRequested();
            if (string.IsNullOrEmpty(entry.Item.ParentPath) ||
                !directoryIds.TryGetValue(entry.Item.ParentPath, out var parent))
            {
                throw new InvalidOperationException(
                    $"Cannot persist file because its parent is missing: {entry.Item.FullPath}");
            }

            if (existingFiles is not null && existingFiles.TryGetValue(entry.Item.FullPath, out var existingFile))
                _db.UpdateFileIdentity(existingFile.Id, entry.Item.FullPath, entry.Item.Name,
                    Path.GetExtension(entry.Item.FullPath).ToLowerInvariant());
            _db.InsertFile(new IndexedFile
            {
                FullPath = entry.Item.FullPath,
                FileName = entry.Item.Name,
                Extension = Path.GetExtension(entry.Item.FullPath).ToLowerInvariant(),
                DirectoryId = parent.Id,
                SizeBytes = entry.Item.SizeBytes ?? 0,
                CreatedTimeUtc = entry.CreatedTimeUtc,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                LastIndexedTimeUtc = now,
                OpenCount = entry.Item.OpenCount,
                IsHidden = entry.IsHidden,
                IsSystem = entry.IsSystem
            });
        }
    }

    private void DeleteCompactPaths(
        IEnumerable<string> paths,
        IIndexCatalogSnapshot current)
    {
        foreach (var path in paths)
        {
            if (current.TryGetItem(path, out var item))
            {
                DeletePersistedPath(path, item.IsDirectory);
            }
            else if (_db.GetDirectoryByPath(path) is not null)
            {
                _db.DeleteDirectory(path);
            }
            else
            {
                _db.DeleteFile(path);
            }
        }
    }

    private static void ValidateCompactPlan(
        ReconciliationSnapshot snapshot,
        IReadOnlyCollection<CompactPreparedEntry> prepared,
        IReadOnlyCollection<string> removed,
        IIndexCatalogSnapshot current,
        CancellationToken ct)
    {
        foreach (var entry in prepared)
        {
            ct.ThrowIfCancellationRequested();
            if (entry.Item.IsDirectory)
            {
                if (!Directory.Exists(entry.Item.FullPath) ||
                    Directory.GetLastWriteTimeUtc(entry.Item.FullPath).Ticks != entry.LastWriteTimeUtc)
                {
                    throw new IOException($"Directory changed during reconciliation: {entry.Item.FullPath}");
                }
            }
            else
            {
                var file = new FileInfo(entry.Item.FullPath);
                if (!file.Exists || file.LastWriteTimeUtc.Ticks != entry.LastWriteTimeUtc ||
                    file.Length != entry.Item.SizeBytes)
                {
                    throw new IOException($"File changed during reconciliation: {entry.Item.FullPath}");
                }
            }
        }

        foreach (var path in removed)
        {
            ct.ThrowIfCancellationRequested();
            if (snapshot.ExcludedScopes.Any(scope => IsSameOrDescendantPath(path, scope)) ||
                snapshot.Entries.ContainsKey(path))
            {
                continue;
            }

            if (!current.TryGetItem(path, out var item))
                continue;
            if (item.IsDirectory ? Directory.Exists(path) : File.Exists(path))
                throw new IOException($"Path reappeared during reconciliation: {path}");
        }
    }

    private static SearchItem CompactDirectoryItem(
        string name,
        string fullPath,
        string? parentPath) =>
        new(
            name,
            fullPath,
            IsDirectory: true,
            SizeBytes: null,
            CreatedTime: null,
            LastWriteTime: null,
            OpenCount: 0,
            parentPath);

    private static string PathName(string path)
    {
        var name = Path.GetFileName(path);
        return string.IsNullOrEmpty(name) ? path : name;
    }

    private void PublishCompactSnapshot(
        IIndexCatalogSnapshot candidate,
        bool incrementalReconciliation)
    {
        var startedAt = DateTime.Now;
        var timestamp = Stopwatch.GetTimestamp();
        Volatile.Write(ref _publishedSearchState, candidate);
        if (incrementalReconciliation)
            Interlocked.Increment(ref _incrementalReconciliationPublishCount);
        Interlocked.Increment(ref _republishCount);
        Interlocked.Exchange(ref _lastRepublishAtTicks, startedAt.Ticks);
        Interlocked.Exchange(
            ref _lastRepublishDurationTicks,
            Stopwatch.GetElapsedTime(timestamp).Ticks);
    }
}
