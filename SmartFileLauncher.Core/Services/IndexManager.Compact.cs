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
                ct.ThrowIfCancellationRequested();
                using (var cacheRead = _db.BeginTransaction())
                {
                    if (!_enforceMeasurementPathSafety &&
                        CompactCatalogCache.TryLoad(DatabasePath, rootPaths, _tokenizer, ct, out var cached))
                    {
                        ReportProgress("Hazır katalog yükleniyor...", 100, cached!.State.ItemCount,
                            started.ElapsedMilliseconds, phase: "cache_catalog");
                        _pathToNode.Clear();
                        _rootNode = null;
                        _detachedNodeCount = cached.DetachedCount;
                        _compactRootAvailable = true;
                        _compactSingleRootPath = cached.SingleRootPath;
                        PublishCompactSnapshot(cached.State, incrementalReconciliation: false);
                        return true;
                    }
                }
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
                    var percentage = totalFiles == 0
                        ? 100
                        : (int)(loadedFiles * 100.0 / totalFiles);
                    var previousPercentage = totalFiles == 0
                        ? 100
                        : (int)((loadedFiles - 1) * 100.0 / totalFiles);
                    if (percentage != previousPercentage)
                    {
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
        CancellationToken ct,
        ReconciliationSnapshot? initialInventory = null)
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

                var snapshot = initialInventory;
                if (snapshot is null)
                {
                    long lastProgress = 0;
                    ReportProgress("Seçili klasörler taranıyor...", 0, 0,
                        started.ElapsedMilliseconds, isIndeterminate: true, phase: "directory_inventory");
                    snapshot = CaptureDiskSnapshot(rootPaths, ct, followReparsePoints: true,
                        progress: count =>
                        {
                            if (started.ElapsedMilliseconds - lastProgress < 250) return;
                            lastProgress = started.ElapsedMilliseconds;
                            ReportProgress("Seçili klasörler taranıyor...", 0, count, lastProgress,
                                isIndeterminate: true, phase: "directory_inventory");
                        });
                }
                void Progress(string phase, string status, int count, int total) =>
                    ReportProgress(status, total == 0 ? 100 : (int)(count * 100L / total),
                        count, started.ElapsedMilliseconds, phase: phase, totalItemCount: total);
                Progress("prepare", "Dosyalar hazırlanıyor...", 0, snapshot.Entries.Count);
                var prepared = PrepareCompactEntries(snapshot, rootPaths, ct,
                    progress: count => Progress("prepare", "Dosyalar hazırlanıyor...", count, snapshot.Entries.Count));
                var searchItems = singleRootPath is null
                    ? prepared.Select(entry => entry.Item)
                    : prepared
                        .Where(entry => !string.Equals(
                            entry.Item.FullPath,
                            singleRootPath,
                            StringComparison.OrdinalIgnoreCase))
                        .Select(entry => entry.Item);
                ReportProgress("Arama kataloğu hazırlanıyor...", 0, prepared.Count,
                    started.ElapsedMilliseconds, isIndeterminate: true, phase: "catalog");
                var candidate = CompactSearchState.Create(searchItems, _tokenizer);

                using var transaction = _db.BeginTransaction();
                try
                {
                    ReportProgress("Önceki indeks temizleniyor...", 0, 0,
                        started.ElapsedMilliseconds, isIndeterminate: true, phase: "clear_database");
                    _db.ClearIndex();
                    Progress("persist", "İndeks kaydediliyor...", 0, prepared.Count);
                    PersistCompactEntries(prepared, ct,
                        progress: count => Progress("persist", "İndeks kaydediliyor...", count, prepared.Count));
                    var rootsKey = string.Join(
                        "|",
                        rootPaths.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
                    _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, rootsKey);
                    _db.SetMetadata(IndexMetadata.Keys.LastFullScanTime, DateTime.UtcNow.Ticks.ToString());
                    _db.SetMetadata(IndexMetadata.Keys.TotalFilesIndexed, prepared.Count.ToString());
                    _db.SetMetadata(IndexMetadata.Keys.InitialInventoryPending,
                        initialInventory is null ? "0" : "1");
                    _db.SetMetadata(IndexMetadata.Keys.LastBootstrapSource,
                        initialInventory is null ? "filesystem" : "mft");
                    _db.SetMetadata(IndexMetadata.Keys.LastBootstrapLinkScopes,
                        initialInventory is null ? "0" : _initialLinkScopes.Count.ToString());
                    ReportProgress("Kayıt tamamlanıyor...", 0, prepared.Count,
                        started.ElapsedMilliseconds, isIndeterminate: true, phase: "commit");
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
        CancellationToken ct,
        bool useSuppliedSnapshot = false)
    {
        _ = suppliedSnapshot;
        lock (_lock)
        {
            var snapshot = useSuppliedSnapshot ? suppliedSnapshot : CaptureDiskSnapshot(rootPaths, ct);
            MergeCompactSnapshotStatus(snapshot, suppliedSnapshot);
            var current = CurrentCompactSnapshot;
            var databaseRoots = rootPaths.Select(path => current.TryGetItem(path, out var item) ? item.FullPath : path).ToArray();
            var directoriesByPath = databaseRoots.SelectMany(_db.GetDirectoriesInScope).DistinctBy(directory => directory.Id).ToDictionary(
                directory => directory.FullPath,
                StringComparer.OrdinalIgnoreCase);
            var filesByPath = databaseRoots.SelectMany(_db.GetFilesInScope).DistinctBy(file => file.Id).ToDictionary(
                file => file.FullPath,
                StringComparer.OrdinalIgnoreCase);
            foreach (var root in rootPaths)
            {
                for (var parent = Path.GetDirectoryName(root); parent is not null &&
                    _activeRootPaths.Any(active => IsSameOrDescendantPath(parent, active)); parent = Path.GetDirectoryName(parent))
                {
                    if (current.TryGetItem(parent, out var parentItem)) parent = parentItem.FullPath;
                    var row = _db.GetDirectoryByPath(parent);
                    if (row is not null) { directoriesByPath[row.FullPath] = row; break; }
                    var info = new DirectoryInfo(parent);
                    if (!info.Exists) break;
                    snapshot.Entries[parent] = new ReconciliationEntry(parent, true,
                        info.LastWriteTimeUtc.Ticks, 0, info.Attributes);
                }
            }

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
                PersistCompactEntries(persistedUpserts, ct, filesByPath, existingDirectories: directoriesByPath);
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
        foreach (var item in EnumerateCompactScopes(current, rootPaths, ct)
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

    private static IEnumerable<SearchItem> EnumerateCompactScopes(IIndexCatalogSnapshot current,
        IReadOnlyList<string> roots, CancellationToken ct)
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pending = new Stack<string>(roots);
        while (pending.TryPop(out var path))
        {
            ct.ThrowIfCancellationRequested();
            if (!seen.Add(path)) continue;
            if (current.TryGetItem(path, out var item)) yield return item;
            foreach (var child in current.GetChildren(path, ct)) pending.Push(child.FullPath);
        }
    }

    private List<CompactPreparedEntry> PrepareCompactEntries(
        ReconciliationSnapshot snapshot,
        IReadOnlyList<string> rootPaths,
        CancellationToken ct,
        IReadOnlyDictionary<string, IndexedFile>? existingFiles = null,
        Action<int>? progress = null)
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
            var attributes = entry.Attributes;
            if (entry.IsDirectory)
            {
                prepared.Add(new CompactPreparedEntry(
                    CompactDirectoryItem(PathName(entry.Path), entry.Path, parentPath),
                    entry.LastWriteTimeUtc,
                    CreatedTimeUtc: 0,
                    IsHidden: (attributes & FileAttributes.Hidden) != 0,
                    IsSystem: (attributes & FileAttributes.System) != 0));
                ReportPrepared();
                continue;
            }

            IndexedFile? persisted = null;
            if (existingFiles is not null)
                existingFiles.TryGetValue(entry.Path, out persisted);
            var createdTimeUtc = persisted?.CreatedTimeUtc ?? entry.CreatedTimeUtc;
            var openCount = persisted?.OpenCount ?? 0;
            if (existingFiles is not null && persisted is null && CurrentCompactSnapshot.TryGetItem(entry.Path, out var cached) && !cached.IsDirectory)
            {
                createdTimeUtc = cached.CreatedTime?.ToUniversalTime().Ticks ?? createdTimeUtc;
                openCount = cached.OpenCount;
            }
            prepared.Add(new CompactPreparedEntry(
                new SearchItem(
                    PathName(entry.Path),
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
            ReportPrepared();
        }

        void ReportPrepared()
        {
            if (progress is not null && (prepared.Count % 1000 == 0 || prepared.Count == snapshot.Entries.Count))
                progress(prepared.Count);
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
        IReadOnlyDictionary<string, IndexedFile>? existingFiles = null,
        Action<int>? progress = null,
        IReadOnlyDictionary<string, IndexedDirectory>? existingDirectories = null)
    {
        var processed = 0;
        void ReportPersisted(int count = 1)
        {
            var previous = processed;
            processed += count;
            if (progress is not null && (previous / 1000 != processed / 1000 || processed == entries.Count))
                progress(processed);
        }
        var now = DateTime.UtcNow.Ticks;
        var directoryIds = (existingDirectories?.Values ?? _db.GetAllDirectories()).ToDictionary(
            directory => directory.FullPath,
            directory => (directory.Id, directory.Depth),
            StringComparer.OrdinalIgnoreCase);
        using var writer = _db.CreatePreparedWriteBatch();

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
            var id = writer.InsertDirectory(new IndexedDirectory
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
            ReportPersisted();
        }

        var files = new List<IndexedFile>(IndexDatabase.PreparedWriteBatch.FileBatchSize);
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
            files.Add(new IndexedFile
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
            if (files.Count == IndexDatabase.PreparedWriteBatch.FileBatchSize)
                FlushFiles();
        }
        if (files.Count != 0)
            FlushFiles();

        void FlushFiles()
        {
            writer.InsertFiles(files, ct);
            ReportPersisted(files.Count);
            files.Clear();
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
