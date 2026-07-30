using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Services;

public class IndexManager : IDisposable
{
    private readonly IndexDatabase _db;
    private readonly FileWatcherService _watcher;
    private readonly ITokenizer _tokenizer;
    private readonly object _lock = new();
    private readonly object _notificationLock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);
    private readonly SemaphoreSlim _reconciliationSignal = new(0, 1);
    private readonly TimeSpan _reconciliationInterval;
    private Task _notificationTask = Task.CompletedTask;
    private CancellationTokenSource? _backgroundSyncCts;
    private Task? _backgroundSyncTask;
    
    private InvertedIndex _invertedIndex;
    private Dictionary<string, FileMetadata> _metadataMap;
    private FileSystemNode? _rootNode;
    private Dictionary<string, FileSystemNode> _pathToNode;
    
    private volatile bool _disposed;
    private bool _isInitialized;
    
    private volatile bool _isDeltaSyncRunning = false;
    private volatile int _deltaSyncProgress = 0;
    private volatile int _deltaSyncTotal = 0;
    private volatile int _deltaSyncProcessed = 0;
    private IReadOnlyList<string> _activeRootPaths = Array.Empty<string>();
    private long _reconciliationRunCount;

    public event Action<IndexProgress>? OnProgress;

    public event Action<FileChangeEvent>? OnFileChange;

    public event Action<string>? OnError;
    
    public event Action<int, int, int>? OnDeltaSyncProgress;

    public IndexManager(ITokenizer? tokenizer = null)
        : this(new IndexDatabase(), new FileWatcherService(), tokenizer)
    {
    }

    internal IndexManager(
        IndexDatabase database,
        FileWatcherService watcher,
        ITokenizer? tokenizer = null,
        TimeSpan? reconciliationInterval = null)
    {
        _tokenizer = tokenizer ?? new BasicTokenizer();
        _db = database;
        _watcher = watcher;
        _reconciliationInterval = reconciliationInterval ?? TimeSpan.FromMinutes(10);
        if (_reconciliationInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        _invertedIndex = new InvertedIndex();
        _metadataMap = new Dictionary<string, FileMetadata>(
            StringComparer.OrdinalIgnoreCase);
        _pathToNode = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);

        _watcher.OnChange += HandleFileChange;
        _watcher.OnError += HandleWatcherError;
    }

    #region Properties

    public InvertedIndex InvertedIndex => _invertedIndex;
    public IReadOnlyDictionary<string, FileMetadata> MetadataMap {
        get {
            lock (_lock) {
                return _metadataMap.ToDictionary(
                    entry => entry.Key,
                    entry => CloneMetadata(entry.Value),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }
    public FileSystemNode? RootNode {
        get {
            lock (_lock) {
                return _rootNode;
            }
        }
    }
    public bool IsInitialized => _isInitialized;
    public string DatabasePath => _db.DatabasePath;

    public int IndexedFileCount => _invertedIndex.NodeCount;
    public int IndexedTokenCount => _invertedIndex.TokenCount;
    
    public bool IsDeltaSyncRunning => _isDeltaSyncRunning;
    public bool IsDeltaSyncComplete => !_isDeltaSyncRunning;
    public int DeltaSyncProgress => _deltaSyncProgress;
    public int DeltaSyncProcessed => _deltaSyncProcessed;
    public int DeltaSyncTotal => _deltaSyncTotal;
    internal long ReconciliationRunCount => Interlocked.Read(ref _reconciliationRunCount);

    #endregion

    #region Initialization

    public async Task InitializeAsync(IEnumerable<string> rootPaths, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await InitializeCoreAsync(rootPaths, ct);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task InitializeCoreAsync(IEnumerable<string> rootPaths, CancellationToken ct)
    {
        await StopBackgroundSyncAsync();
        _watcher.Stop();
        _watcher.ClearWatches();

        var sw = Stopwatch.StartNew();
        var paths = NormalizeRootPaths(rootPaths);
        
        ReportProgress("Başlatılıyor...", 0, 0, 0);

        _db.Open();

        var cachedRoot = _db.GetMetadata(IndexMetadata.Keys.ScanRootPath);
        var newRootsKey = string.Join("|", paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        var hasCache = cachedRoot != null && 
                       cachedRoot.Equals(newRootsKey, StringComparison.OrdinalIgnoreCase) &&
                       _db.GetFileCount() > 0;

        if (hasCache)
        {
            ReportProgress("Önbellekten yükleniyor...", 0, 0, 0);
            await LoadFromCacheMultiAsync(paths, ct);
        }
        else
        {
            ReportProgress("İlk kurulum - dosyalar taranıyor...", 0, 0, 0);
            await BootstrapScanMultiAsync(paths, ct);
        }

        _activeRootPaths = paths;

        SetupWatchers(paths);

        sw.Stop();
        _db.SetMetadata(IndexMetadata.Keys.LastBuildDurationMs, sw.ElapsedMilliseconds.ToString());
        _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, newRootsKey);

        _isInitialized = true;
        StartBackgroundReconciliation(paths);
        ReportProgress("Hazır", 100, _invertedIndex.NodeCount, sw.ElapsedMilliseconds);
    }

    public async Task InitializeAsync(string rootPath, CancellationToken ct = default)
    {
        await InitializeAsync(new[] { rootPath }, ct);
    }

    public async Task RescanAsync(string rootPath, CancellationToken ct = default)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopBackgroundSyncAsync();
            _watcher.Stop();
            _watcher.ClearWatches();
            _db.ClearIndex();
            lock (_lock)
            {
                ResetInMemoryIndex();
            }

            var normalizedRootPath = NormalizeIndexedPath(rootPath);
            await BootstrapScanAsync(normalizedRootPath, ct);
            var paths = new List<string> { normalizedRootPath };
            _activeRootPaths = paths;
            SetupWatchers(paths);
            StartBackgroundReconciliation(paths);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    #endregion

    #region Bootstrap Scan

    private async Task BootstrapScanAsync(string rootPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        _db.ClearIndex();
        lock (_lock)
        {
            ResetInMemoryIndex();
        }

        var rootName = Path.GetFileName(rootPath);
        if (string.IsNullOrEmpty(rootName)) rootName = rootPath;
        _rootNode = new FileSystemNode(rootName, rootPath, true);
        _pathToNode[rootPath] = _rootNode;

        int totalItems = 0;
        int processedItems = 0;

        await Task.Run(() =>
        {
            try
            {
                totalItems = CountItems(rootPath);
            }
            catch
            {
                totalItems = 100;
            }

            using var transaction = _db.BeginTransaction();

            try
            {
                var rootDir = new IndexedDirectory
                {
                    FullPath = rootPath,
                    Name = rootName,
                    ParentId = null,
                    Depth = 0,
                    LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(rootPath).Ticks,
                    LastIndexedTimeUtc = DateTime.UtcNow.Ticks
                };
                var rootDirId = _db.InsertDirectory(rootDir);

                ScanDirectoryRecursive(rootPath, _rootNode, rootDirId, 1, ref processedItems, totalItems, ct);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, ct);

        _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, rootPath);
        _db.SetMetadata(IndexMetadata.Keys.LastFullScanTime, DateTime.UtcNow.Ticks.ToString());
        _db.SetMetadata(IndexMetadata.Keys.TotalFilesIndexed, _invertedIndex.NodeCount.ToString());

        sw.Stop();
        ReportProgress("Tarama tamamlandı", 100, _invertedIndex.NodeCount, sw.ElapsedMilliseconds);
    }

    private async Task BootstrapScanMultiAsync(List<string> rootPaths, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        _db.ClearIndex();
        lock (_lock)
        {
            ResetInMemoryIndex();
        }

        _rootNode = new FileSystemNode("Root", "", true);

        int totalItems = 0;
        int processedItems = 0;

        await Task.Run(() =>
        {
            foreach (var rootPath in rootPaths)
            {
                if (!Directory.Exists(rootPath)) continue;
                try
                {
                    totalItems += CountItems(rootPath);
                }
                catch
                {
                    totalItems += 100;
                }
            }

            using var transaction = _db.BeginTransaction();

            try
            {
                foreach (var rootPath in rootPaths)
                {
                    if (!Directory.Exists(rootPath)) continue;
                    
                    var rootName = Path.GetFileName(rootPath);
                    if (string.IsNullOrEmpty(rootName)) rootName = rootPath;
                    
                    var rootPathNode = new FileSystemNode(rootName, rootPath, true);
                    _rootNode.AddChild(rootPathNode);
                    _pathToNode[rootPath] = rootPathNode;

                    var rootDir = new IndexedDirectory
                    {
                        FullPath = rootPath,
                        Name = rootName,
                        ParentId = null,
                        Depth = 0,
                        LastWriteTimeUtc = Directory.GetLastWriteTimeUtc(rootPath).Ticks,
                        LastIndexedTimeUtc = DateTime.UtcNow.Ticks
                    };
                    var rootDirId = _db.InsertDirectory(rootDir);

                    ScanDirectoryRecursive(rootPath, rootPathNode, rootDirId, 1, ref processedItems, totalItems, ct);
                }

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, ct);

        var rootsKey = string.Join("|", rootPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, rootsKey);
        _db.SetMetadata(IndexMetadata.Keys.LastFullScanTime, DateTime.UtcNow.Ticks.ToString());
        _db.SetMetadata(IndexMetadata.Keys.TotalFilesIndexed, _invertedIndex.NodeCount.ToString());

        sw.Stop();
        ReportProgress("Tarama tamamlandı", 100, _invertedIndex.NodeCount, sw.ElapsedMilliseconds);
    }

    private void ScanDirectoryRecursive(string path, FileSystemNode parentNode, long parentDirId, 
                                        int depth, ref int processedItems, int totalItems, CancellationToken ct,
                                        bool reportProgress = true)
    {
        ct.ThrowIfCancellationRequested();

        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                ct.ThrowIfCancellationRequested();

                var dirName = Path.GetFileName(dir);
                var dirInfo = new DirectoryInfo(dir);

                if ((dirInfo.Attributes & FileAttributes.Hidden) != 0 ||
                    (dirInfo.Attributes & FileAttributes.System) != 0)
                    continue;

                var dirNode = new FileSystemNode(dirName, dir, true);
                parentNode.AddChild(dirNode);
                _pathToNode[dir] = dirNode;

                IndexNode(dirNode);

                var indexedDir = new IndexedDirectory
                {
                    FullPath = dir,
                    Name = dirName,
                    ParentId = parentDirId,
                    Depth = depth,
                    LastWriteTimeUtc = dirInfo.LastWriteTimeUtc.Ticks,
                    LastIndexedTimeUtc = DateTime.UtcNow.Ticks,
                    IsHidden = (dirInfo.Attributes & FileAttributes.Hidden) != 0
                };
                var dirId = _db.InsertDirectory(indexedDir);

                processedItems++;
                if (reportProgress && processedItems % 50 == 0)
                {
                    int pct = Math.Min(99, (int)(processedItems * 100.0 / totalItems));
                    ReportProgress($"Taranıyor: {dirName}", pct, processedItems, 0);
                }

                ScanDirectoryRecursive(
                    dir,
                    dirNode,
                    dirId,
                    depth + 1,
                    ref processedItems,
                    totalItems,
                    ct,
                    reportProgress);
            }

            foreach (var file in Directory.GetFiles(path))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var fi = new FileInfo(file);

                    if ((fi.Attributes & FileAttributes.Hidden) != 0 ||
                        (fi.Attributes & FileAttributes.System) != 0)
                        continue;

                    var fileNode = new FileSystemNode(fi.Name, file, false)
                    {
                        Metadata = new FileMetadata
                        {
                            SizeBytes = fi.Length,
                            CreatedTime = fi.CreationTime,
                            LastWriteTime = fi.LastWriteTime
                        }
                    };
                    parentNode.AddChild(fileNode);
                    _pathToNode[file] = fileNode;

                    IndexNode(fileNode);

                    _metadataMap[file] = fileNode.Metadata!;

                    var indexedFile = new IndexedFile
                    {
                        FullPath = file,
                        FileName = fi.Name,
                        Extension = fi.Extension.ToLowerInvariant(),
                        DirectoryId = parentDirId,
                        SizeBytes = fi.Length,
                        CreatedTimeUtc = fi.CreationTimeUtc.Ticks,
                        LastWriteTimeUtc = fi.LastWriteTimeUtc.Ticks,
                        LastIndexedTimeUtc = DateTime.UtcNow.Ticks,
                        IsHidden = (fi.Attributes & FileAttributes.Hidden) != 0,
                        IsSystem = (fi.Attributes & FileAttributes.System) != 0
                    };
                    _db.InsertFile(indexedFile);

                    processedItems++;
                }
                catch (UnauthorizedAccessException) { }
                catch (IOException) { }
            }
        }
        catch (UnauthorizedAccessException) { }
        catch (IOException) { }
    }

    private int CountItems(string path)
    {
        int count = 0;
        try
        {
            count += Directory.GetFiles(path).Length;
            foreach (var dir in Directory.GetDirectories(path))
            {
                count++;
                count += CountItems(dir);
            }
        }
        catch { }
        return count;
    }

    #endregion

    #region Cache Loading

    private async Task LoadFromCacheAsync(string rootPath, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        lock (_lock)
        {
            ResetInMemoryIndex();
        }

        await Task.Run(() =>
        {
            var rootName = Path.GetFileName(rootPath);
            if (string.IsNullOrEmpty(rootName)) rootName = rootPath;
            _rootNode = new FileSystemNode(rootName, rootPath, true);
            _pathToNode[rootPath] = _rootNode;

            var dirMap = new Dictionary<long, (IndexedDirectory Dir, FileSystemNode Node)>();
            long? rootDirId = null;

            foreach (var dir in _db.GetAllDirectories())
            {
                ct.ThrowIfCancellationRequested();

                if (dir.FullPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    rootDirId = dir.Id;
                    dirMap[dir.Id] = (dir, _rootNode);
                    IndexNode(_rootNode);
                    continue;
                }

                var node = new FileSystemNode(dir.Name, dir.FullPath, true);
                _pathToNode[dir.FullPath] = node;
                dirMap[dir.Id] = (dir, node);

                IndexNode(node);
            }

            foreach (var (id, (dir, node)) in dirMap)
            {
                if (node == _rootNode) continue;
                
                if (dir.ParentId.HasValue && dirMap.TryGetValue(dir.ParentId.Value, out var parent))
                {
                    parent.Node.AddChild(node);
                }
                else
                {
                    _rootNode.AddChild(node);
                }
            }

            int fileCount = 0;
            int totalFiles = _db.GetFileCount();

            foreach (var file in _db.GetAllFiles())
            {
                ct.ThrowIfCancellationRequested();

                var node = new FileSystemNode(file.FileName, file.FullPath, false)
                {
                    Metadata = new FileMetadata
                    {
                        SizeBytes = file.SizeBytes,
                        CreatedTime = file.CreatedTime,
                        LastWriteTime = file.LastWriteTime,
                        OpenCount = file.OpenCount
                    }
                };

                _pathToNode[file.FullPath] = node;
                _metadataMap[file.FullPath] = node.Metadata!;

                if (file.DirectoryId > 0 && dirMap.TryGetValue(file.DirectoryId, out var parentDir))
                {
                    parentDir.Node.AddChild(node);
                }
                else
                {
                    var parentPath = Path.GetDirectoryName(file.FullPath);
                    if (parentPath != null && _pathToNode.TryGetValue(parentPath, out var parentNode))
                    {
                        parentNode.AddChild(node);
                    }
                    else if (parentPath != null && 
                             string.Equals(parentPath, rootPath, StringComparison.OrdinalIgnoreCase))
                    {
                        _rootNode.AddChild(node);
                    }
                }

                IndexNode(node);

                fileCount++;
                if (fileCount % 100 == 0)
                {
                    int pct = (int)(fileCount * 100.0 / totalFiles);
                    ReportProgress($"Önbellek yükleniyor: {fileCount}/{totalFiles}", pct, fileCount, 0);
                }
            }
        }, ct);

        sw.Stop();
        ReportProgress("Önbellek yüklendi", 100, _invertedIndex.NodeCount, sw.ElapsedMilliseconds);
    }

    private async Task LoadFromCacheMultiAsync(List<string> rootPaths, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        lock (_lock)
        {
            ResetInMemoryIndex();
        }

        await Task.Run(() =>
        {
            _rootNode = new FileSystemNode("Root", "", true);

            var rootPathNodes = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);
            foreach (var rootPath in rootPaths)
            {
                if (!Directory.Exists(rootPath)) continue;
                
                var rootName = Path.GetFileName(rootPath);
                if (string.IsNullOrEmpty(rootName)) rootName = rootPath;
                
                var node = new FileSystemNode(rootName, rootPath, true);
                _rootNode.AddChild(node);
                rootPathNodes[rootPath] = node;
                _pathToNode[rootPath] = node;
            }

            var dirMap = new Dictionary<long, (IndexedDirectory Dir, FileSystemNode Node)>();

            foreach (var dir in _db.GetAllDirectories())
            {
                ct.ThrowIfCancellationRequested();

                if (rootPathNodes.TryGetValue(dir.FullPath, out var existingRootNode))
                {
                    dirMap[dir.Id] = (dir, existingRootNode);
                    IndexNode(existingRootNode);
                    continue;
                }

                var node = new FileSystemNode(dir.Name, dir.FullPath, true);
                _pathToNode[dir.FullPath] = node;
                dirMap[dir.Id] = (dir, node);
                IndexNode(node);
            }

            foreach (var (id, (dir, node)) in dirMap)
            {
                if (rootPathNodes.ContainsKey(dir.FullPath)) continue;
                
                if (dir.ParentId.HasValue && dirMap.TryGetValue(dir.ParentId.Value, out var parent))
                {
                    parent.Node.AddChild(node);
                }
                else
                {
                    var matchingRoot = rootPathNodes.Keys.FirstOrDefault(rp => 
                        dir.FullPath.StartsWith(rp, StringComparison.OrdinalIgnoreCase));
                    
                    if (matchingRoot != null && rootPathNodes.TryGetValue(matchingRoot, out var rootNode))
                    {
                        rootNode.AddChild(node);
                    }
                    else
                    {
                        _rootNode.AddChild(node);
                    }
                }
            }

            int fileCount = 0;
            int totalFiles = _db.GetFileCount();

            foreach (var file in _db.GetAllFiles())
            {
                ct.ThrowIfCancellationRequested();

                var node = new FileSystemNode(file.FileName, file.FullPath, false)
                {
                    Metadata = new FileMetadata
                    {
                        SizeBytes = file.SizeBytes,
                        CreatedTime = file.CreatedTime,
                        LastWriteTime = file.LastWriteTime,
                        OpenCount = file.OpenCount
                    }
                };

                _pathToNode[file.FullPath] = node;
                _metadataMap[file.FullPath] = node.Metadata!;

                if (file.DirectoryId > 0 && dirMap.TryGetValue(file.DirectoryId, out var parentDir))
                {
                    parentDir.Node.AddChild(node);
                }
                else
                {
                    var parentPath = Path.GetDirectoryName(file.FullPath);
                    if (parentPath != null && _pathToNode.TryGetValue(parentPath, out var parentNode))
                    {
                        parentNode.AddChild(node);
                    }
                    else if (parentPath != null)
                    {
                        foreach (var rootPath in rootPathNodes.Keys)
                        {
                            if (string.Equals(parentPath, rootPath, StringComparison.OrdinalIgnoreCase))
                            {
                                rootPathNodes[rootPath].AddChild(node);
                                break;
                            }
                        }
                    }
                }

                IndexNode(node);

                fileCount++;
                if (fileCount % 100 == 0)
                {
                    int pct = (int)(fileCount * 100.0 / totalFiles);
                    ReportProgress($"Önbellek yükleniyor: {fileCount}/{totalFiles}", pct, fileCount, 0);
                }
            }
        }, ct);

        sw.Stop();
        ReportProgress("Önbellek yüklendi", 100, _invertedIndex.NodeCount, sw.ElapsedMilliseconds);
    }

    #endregion

    #region Reconciliation

    private void StartBackgroundReconciliation(List<string> rootPaths)
    {
        while (_reconciliationSignal.Wait(0)) { }

        var syncCts = new CancellationTokenSource();
        _backgroundSyncCts = syncCts;
        _backgroundSyncTask = Task.Run(
            () => BackgroundReconciliationLoopAsync(rootPaths, syncCts.Token),
            syncCts.Token);
    }

    private async Task BackgroundReconciliationLoopAsync(
        IReadOnlyList<string> rootPaths,
        CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            try
            {
                await ReconcilePathsAsync(rootPaths, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                NotifyError($"Background reconciliation error: {ex.Message}");
            }

            try
            {
                await _reconciliationSignal
                    .WaitAsync(_reconciliationInterval, ct)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                break;
            }
        }
    }

    private async Task StopBackgroundSyncAsync()
    {
        var syncCts = _backgroundSyncCts;
        var syncTask = _backgroundSyncTask;
        _backgroundSyncCts = null;
        _backgroundSyncTask = null;

        if (syncCts == null && syncTask == null)
            return;

        try
        {
            syncCts?.Cancel();
            if (syncTask != null)
            {
                await syncTask.ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            NotifyError($"Background reconciliation shutdown error: {ex.Message}");
        }
        finally
        {
            syncCts?.Dispose();
        }
    }

    public async Task<bool> EnsureSyncedAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        try
        {
            return await ReconcilePathsAsync(
                    new[] { NormalizeIndexedPath(path) },
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            NotifyError($"On-demand reconciliation error for {path}: {ex.Message}");
            return false;
        }
    }

    private async Task<bool> ReconcilePathsAsync(
        IReadOnlyList<string> rootPaths,
        CancellationToken ct)
    {
        var normalizedRoots = NormalizeRootPaths(rootPaths);
        if (normalizedRoots.Count == 0)
            return true;

        await _reconciliationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            _isDeltaSyncRunning = true;
            _deltaSyncProgress = 0;
            _deltaSyncProcessed = 0;
            _deltaSyncTotal = 0;

            var snapshot = await Task.Run(
                    () => CaptureDiskSnapshot(normalizedRoots, ct),
                    ct)
                .ConfigureAwait(false);

            _deltaSyncTotal = snapshot.Entries.Count;
            var changes = ApplyReconciliationSnapshot(
                normalizedRoots,
                snapshot,
                ct);

            _deltaSyncProcessed = _deltaSyncTotal;
            _deltaSyncProgress = 100;
            Interlocked.Increment(ref _reconciliationRunCount);
            NotifyDeltaSyncProgress(
                _deltaSyncProcessed,
                _deltaSyncTotal,
                _deltaSyncProgress);

            if (changes > 0)
            {
                ReportProgress(
                    $"İndeks uzlaştırıldı: {changes} değişiklik.",
                    100,
                    _invertedIndex.NodeCount,
                    0);
            }

            if (snapshot.Errors.Count > 0)
            {
                NotifyError(
                    $"Reconciliation skipped {snapshot.ProtectedScopes.Count} inaccessible scope(s): " +
                    string.Join(" | ", snapshot.Errors.Take(3)));
            }

            return snapshot.ProtectedScopes.Count == 0;
        }
        finally
        {
            _isDeltaSyncRunning = false;
            _reconciliationGate.Release();
        }
    }

    private int ApplyReconciliationSnapshot(
        IReadOnlyList<string> rootPaths,
        ReconciliationSnapshot snapshot,
        CancellationToken ct)
    {
        lock (_lock)
        {
            var changes = 0;
            var cachedNodes = _pathToNode.Values
                .Where(node => rootPaths.Any(root =>
                    IsSameOrDescendantPath(node.FullPath, root)))
                .OrderBy(node => node.FullPath.Length)
                .ToList();

            var removedDirectories = new List<string>();
            foreach (var node in cachedNodes)
            {
                ct.ThrowIfCancellationRequested();

                if (rootPaths.Any(root =>
                        string.Equals(root, node.FullPath, StringComparison.OrdinalIgnoreCase)))
                {
                    continue;
                }

                if (removedDirectories.Any(parent =>
                        IsSameOrDescendantPath(node.FullPath, parent)))
                {
                    continue;
                }

                if (!ShouldRemoveCachedNode(node, snapshot))
                    continue;

                DeletePersistedPath(node.FullPath, node.IsDirectory);
                RemoveFromIndex(node.FullPath);
                changes++;

                if (node.IsDirectory)
                {
                    removedDirectories.Add(node.FullPath);
                }
            }

            foreach (var entry in snapshot.Entries.Values
                         .Where(entry => entry.IsDirectory)
                         .OrderBy(entry => entry.Path.Length))
            {
                ct.ThrowIfCancellationRequested();

                if (_pathToNode.TryGetValue(entry.Path, out var existing))
                {
                    if (existing.IsDirectory)
                    {
                        changes += UpdatePersistedDirectory(existing, entry);
                    }
                    continue;
                }

                if (snapshot.ProtectedScopes.Any(scope =>
                        IsSameOrDescendantPath(entry.Path, scope)))
                {
                    continue;
                }

                if (rootPaths.Any(root =>
                        string.Equals(root, entry.Path, StringComparison.OrdinalIgnoreCase)))
                {
                    AddRootDirectoryToIndex(entry.Path, ct);
                }
                else
                {
                    AddPathToIndex(entry.Path, isDirectory: true, ct);
                }

                if (_pathToNode.ContainsKey(entry.Path))
                {
                    changes++;
                }
            }

            foreach (var entry in snapshot.Entries.Values
                         .Where(entry => !entry.IsDirectory)
                         .OrderBy(entry => entry.Path.Length))
            {
                ct.ThrowIfCancellationRequested();

                if (!_pathToNode.TryGetValue(entry.Path, out var existing))
                {
                    AddPathToIndex(entry.Path, isDirectory: false, ct);
                    if (_pathToNode.ContainsKey(entry.Path))
                    {
                        changes++;
                    }
                    continue;
                }

                if (existing.IsDirectory)
                    continue;

                changes += UpdatePersistedFile(existing, entry);
            }

            return changes;
        }
    }

    private bool ShouldRemoveCachedNode(
        FileSystemNode node,
        ReconciliationSnapshot snapshot)
    {
        if (snapshot.Entries.TryGetValue(node.FullPath, out var diskEntry))
            return diskEntry.IsDirectory != node.IsDirectory;

        if (snapshot.ExcludedScopes.Any(scope =>
                IsSameOrDescendantPath(node.FullPath, scope)))
        {
            return true;
        }

        if (snapshot.ProtectedScopes.Any(scope =>
                IsSameOrDescendantPath(node.FullPath, scope)))
        {
            return false;
        }

        return node.IsDirectory
            ? !Directory.Exists(node.FullPath)
            : !File.Exists(node.FullPath);
    }

    private int UpdatePersistedDirectory(
        FileSystemNode node,
        ReconciliationEntry entry)
    {
        var persisted = _db.GetDirectoryByPath(entry.Path);
        if (persisted == null)
        {
            var parentPath = node.Parent?.FullPath;
            var parent = string.IsNullOrEmpty(parentPath)
                ? null
                : _db.GetDirectoryByPath(parentPath);
            if (!string.IsNullOrEmpty(parentPath) && parent == null)
                return 0;

            var directoryInfo = new DirectoryInfo(entry.Path);
            persisted = new IndexedDirectory
            {
                FullPath = entry.Path,
                Name = node.Name,
                ParentId = parent?.Id,
                Depth = parent?.Depth + 1 ?? 0,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                LastIndexedTimeUtc = DateTime.UtcNow.Ticks,
                IsHidden = (directoryInfo.Attributes & FileAttributes.Hidden) != 0
            };
            _db.InsertDirectory(persisted);
            return 1;
        }

        if (persisted.LastWriteTimeUtc == entry.LastWriteTimeUtc)
        {
            return 0;
        }

        persisted.LastWriteTimeUtc = entry.LastWriteTimeUtc;
        persisted.LastIndexedTimeUtc = DateTime.UtcNow.Ticks;
        _db.InsertDirectory(persisted);
        return 1;
    }

    private int UpdatePersistedFile(
        FileSystemNode node,
        ReconciliationEntry entry)
    {
        var persisted = _db.GetFileByPath(entry.Path);
        if (persisted == null)
        {
            var parentPath = node.Parent?.FullPath;
            var parent = string.IsNullOrEmpty(parentPath)
                ? null
                : _db.GetDirectoryByPath(parentPath);
            if (parent == null)
                return 0;

            var fileInfo = new FileInfo(entry.Path);
            persisted = new IndexedFile
            {
                FullPath = entry.Path,
                FileName = node.Name,
                Extension = fileInfo.Extension.ToLowerInvariant(),
                DirectoryId = parent.Id,
                SizeBytes = entry.SizeBytes,
                CreatedTimeUtc = fileInfo.CreationTimeUtc.Ticks,
                LastWriteTimeUtc = entry.LastWriteTimeUtc,
                LastIndexedTimeUtc = DateTime.UtcNow.Ticks,
                OpenCount = node.Metadata?.OpenCount ?? 0,
                IsHidden = (fileInfo.Attributes & FileAttributes.Hidden) != 0,
                IsSystem = (fileInfo.Attributes & FileAttributes.System) != 0
            };
            _db.InsertFile(persisted);

            if (node.Metadata != null)
            {
                node.Metadata.SizeBytes = entry.SizeBytes;
                node.Metadata.LastWriteTime = new DateTime(
                        entry.LastWriteTimeUtc,
                        DateTimeKind.Utc)
                    .ToLocalTime();
            }

            return 1;
        }

        if (persisted.LastWriteTimeUtc == entry.LastWriteTimeUtc &&
            persisted.SizeBytes == entry.SizeBytes)
        {
            return 0;
        }

        persisted.LastWriteTimeUtc = entry.LastWriteTimeUtc;
        persisted.SizeBytes = entry.SizeBytes;
        persisted.LastIndexedTimeUtc = DateTime.UtcNow.Ticks;
        _db.InsertFile(persisted);

        if (node.Metadata != null)
        {
            node.Metadata.SizeBytes = entry.SizeBytes;
            node.Metadata.LastWriteTime = new DateTime(
                    entry.LastWriteTimeUtc,
                    DateTimeKind.Utc)
                .ToLocalTime();
        }

        return 1;
    }

    private void AddRootDirectoryToIndex(string rootPath, CancellationToken ct)
    {
        if (_pathToNode.ContainsKey(rootPath) || !Directory.Exists(rootPath))
            return;

        var directoryInfo = new DirectoryInfo(rootPath);
        var node = new FileSystemNode(directoryInfo.Name, rootPath, true);
        _rootNode ??= new FileSystemNode("Root", "", true);

        using var transaction = _db.BeginTransaction();
        try
        {
            var rootDirectory = new IndexedDirectory
            {
                FullPath = rootPath,
                Name = directoryInfo.Name,
                ParentId = null,
                Depth = 0,
                LastWriteTimeUtc = directoryInfo.LastWriteTimeUtc.Ticks,
                LastIndexedTimeUtc = DateTime.UtcNow.Ticks,
                IsHidden = (directoryInfo.Attributes & FileAttributes.Hidden) != 0
            };
            var rootId = _db.InsertDirectory(rootDirectory);

            _rootNode.AddChild(node);
            _pathToNode[rootPath] = node;
            IndexNode(node);

            var processedItems = 0;
            ScanDirectoryRecursive(
                rootPath,
                node,
                rootId,
                1,
                ref processedItems,
                totalItems: 1,
                ct,
                reportProgress: false);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            RemoveFromIndex(rootPath);
            throw;
        }
    }

    private static ReconciliationSnapshot CaptureDiskSnapshot(
        IReadOnlyList<string> rootPaths,
        CancellationToken ct)
    {
        var snapshot = new ReconciliationSnapshot();

        foreach (var rootPath in rootPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (Directory.Exists(rootPath))
            {
                CaptureDirectoryTree(rootPath, snapshot, ct);
            }
            else if (File.Exists(rootPath))
            {
                CaptureFile(rootPath, snapshot);
            }
        }

        return snapshot;
    }

    private static void CaptureDirectoryTree(
        string rootPath,
        ReconciliationSnapshot snapshot,
        CancellationToken ct)
    {
        var pending = new Stack<string>();
        pending.Push(rootPath);

        while (pending.Count > 0)
        {
            ct.ThrowIfCancellationRequested();
            var directoryPath = pending.Pop();

            FileAttributes attributes;
            try
            {
                attributes = File.GetAttributes(directoryPath);
                if (!string.Equals(
                        directoryPath,
                        rootPath,
                        StringComparison.OrdinalIgnoreCase) &&
                    IsHiddenOrSystem(attributes))
                {
                    snapshot.ExcludedScopes.Add(directoryPath);
                    continue;
                }

                var directoryInfo = new DirectoryInfo(directoryPath);
                snapshot.Entries[directoryPath] = new ReconciliationEntry(
                    directoryPath,
                    IsDirectory: true,
                    directoryInfo.LastWriteTimeUtc.Ticks,
                    SizeBytes: 0);

                if ((attributes & FileAttributes.ReparsePoint) != 0)
                {
                    snapshot.ProtectedScopes.Add(directoryPath);
                    snapshot.Errors.Add($"{directoryPath}: reparse point traversal skipped");
                    continue;
                }
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException)
            {
                if (Directory.Exists(directoryPath))
                {
                    snapshot.ProtectedScopes.Add(directoryPath);
                    snapshot.Errors.Add($"{directoryPath}: {ex.Message}");
                }
                continue;
            }

            string[] entries;
            try
            {
                entries = Directory.GetFileSystemEntries(directoryPath);
            }
            catch (Exception ex) when (
                ex is UnauthorizedAccessException or IOException)
            {
                if (Directory.Exists(directoryPath))
                {
                    snapshot.ProtectedScopes.Add(directoryPath);
                    snapshot.Errors.Add($"{directoryPath}: {ex.Message}");
                }
                continue;
            }

            foreach (var path in entries)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var entryAttributes = File.GetAttributes(path);
                    if (IsHiddenOrSystem(entryAttributes))
                    {
                        snapshot.ExcludedScopes.Add(path);
                        continue;
                    }

                    if ((entryAttributes & FileAttributes.Directory) != 0)
                    {
                        pending.Push(NormalizeIndexedPath(path));
                    }
                    else
                    {
                        CaptureFile(path, snapshot);
                    }
                }
                catch (Exception ex) when (
                    ex is UnauthorizedAccessException or IOException)
                {
                    if (Directory.Exists(path) || File.Exists(path))
                    {
                        var normalizedPath = NormalizeIndexedPath(path);
                        snapshot.ProtectedScopes.Add(normalizedPath);
                        snapshot.Errors.Add($"{normalizedPath}: {ex.Message}");
                    }
                }
            }
        }
    }

    private static void CaptureFile(
        string filePath,
        ReconciliationSnapshot snapshot)
    {
        var normalizedPath = NormalizeIndexedPath(filePath);
        try
        {
            var fileInfo = new FileInfo(normalizedPath);
            snapshot.Entries[normalizedPath] = new ReconciliationEntry(
                normalizedPath,
                IsDirectory: false,
                fileInfo.LastWriteTimeUtc.Ticks,
                fileInfo.Length);
        }
        catch (Exception ex) when (
            ex is UnauthorizedAccessException or IOException)
        {
            if (File.Exists(normalizedPath))
            {
                snapshot.ProtectedScopes.Add(normalizedPath);
                snapshot.Errors.Add($"{normalizedPath}: {ex.Message}");
            }
        }
    }

    private static bool IsHiddenOrSystem(FileAttributes attributes) =>
        (attributes & (FileAttributes.Hidden | FileAttributes.System)) != 0;

    private sealed record ReconciliationEntry(
        string Path,
        bool IsDirectory,
        long LastWriteTimeUtc,
        long SizeBytes);

    private sealed class ReconciliationSnapshot
    {
        public Dictionary<string, ReconciliationEntry> Entries { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ProtectedScopes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public HashSet<string> ExcludedScopes { get; } =
            new(StringComparer.OrdinalIgnoreCase);

        public List<string> Errors { get; } = new();
    }

    #endregion

    #region FileWatcher Integration

    private void HandleWatcherError(Exception exception)
    {
        NotifyError(exception.Message);
        RequestReconciliation();
    }

    private void RequestReconciliation()
    {
        if (_disposed || !_isInitialized || _activeRootPaths.Count == 0)
            return;

        try
        {
            if (_reconciliationSignal.CurrentCount == 0)
            {
                _reconciliationSignal.Release();
            }
        }
        catch (SemaphoreFullException)
        {
        }
    }

    private void SetupWatchers(IEnumerable<string> rootPaths)
    {
        _watcher.Stop();
        _watcher.ClearWatches();

        var configured = false;
        var configuredRoots = new List<string>();
        var normalizedRoots = NormalizeRootPaths(rootPaths)
            .Where(Directory.Exists)
            .OrderBy(path => path.Length);

        foreach (var rootPath in normalizedRoots)
        {
            if (configuredRoots.Any(parent => IsSameOrDescendantPath(rootPath, parent)))
                continue;

            _watcher.Watch(rootPath);
            configuredRoots.Add(rootPath);
            configured = true;
        }

        if (configured)
        {
            _watcher.Start();
        }
    }

    private static List<string> NormalizeRootPaths(IEnumerable<string> rootPaths)
    {
        var normalized = rootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(NormalizeIndexedPath)
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(path => path.Length);

        var roots = new List<string>();
        foreach (var path in normalized)
        {
            if (!roots.Any(parent => IsSameOrDescendantPath(path, parent)))
            {
                roots.Add(path);
            }
        }

        return roots;
    }

    private static bool IsSameOrDescendantPath(string candidatePath, string parentPath)
    {
        if (string.Equals(candidatePath, parentPath, StringComparison.OrdinalIgnoreCase))
            return true;

        var prefix = parentPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)
                     + Path.DirectorySeparatorChar;
        return candidatePath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    internal void ApplyFileChange(FileChangeEvent evt) => HandleFileChange(evt);

    private void HandleFileChange(FileChangeEvent evt)
    {
        string? error = null;
        var processed = false;

        lock (_lock)
        {
            try
            {
                switch (evt.ChangeType)
                {
                    case FileChangeType.Created:
                        HandleCreated(evt);
                        break;

                    case FileChangeType.Deleted:
                        HandleDeleted(evt);
                        break;

                    case FileChangeType.Renamed:
                        HandleRenamed(evt);
                        break;

                    case FileChangeType.Modified:
                        HandleModified(evt);
                        break;
                }
                processed = true;
            }
            catch (Exception ex)
            {
                error = $"Error handling {evt.ChangeType}: {ex.Message}";
            }
        }

        if (error != null)
        {
            NotifyError(error);
        }
        else if (processed)
        {
            QueueNotification(() => OnFileChange?.Invoke(evt));
        }
    }

    private void HandleCreated(FileChangeEvent evt)
    {
        var isDirectory = Directory.Exists(evt.FullPath);
        if (!isDirectory && !File.Exists(evt.FullPath))
        {
            isDirectory = evt.IsDirectory;
        }

        AddPathToIndex(evt.FullPath, isDirectory);
    }

    private void AddPathToIndex(
        string path,
        bool isDirectory,
        CancellationToken ct = default)
    {
        path = NormalizeIndexedPath(path);

        if (_pathToNode.TryGetValue(path, out var existingNode))
        {
            if (existingNode.IsDirectory == isDirectory)
            {
                return;
            }

            DeletePersistedPath(existingNode.FullPath, existingNode.IsDirectory);
            RemoveFromIndex(existingNode.FullPath);
        }

        var parentPath = Path.GetDirectoryName(path);
        if (parentPath == null || !_pathToNode.TryGetValue(parentPath, out var parentNode))
        {
            return;
        }

        if (isDirectory)
        {
            AddDirectoryTreeToIndex(path, parentNode, ct);
        }
        else
        {
            AddFileToIndex(path, parentNode);
        }
    }

    private void HandleDeleted(FileChangeEvent evt)
    {
        var eventPath = NormalizeIndexedPath(evt.FullPath);
        _pathToNode.TryGetValue(eventPath, out var existingNode);
        var persistedPath = existingNode?.FullPath ?? eventPath;
        var isDirectory = ResolveIsDirectory(persistedPath, existingNode, evt.IsDirectory);

        DeletePersistedPath(persistedPath, isDirectory);
        RemoveFromIndex(persistedPath);
    }

    private void HandleRenamed(FileChangeEvent evt)
    {
        if (evt.OldPath == null)
        {
            HandleCreated(evt);
            return;
        }

        var oldPath = NormalizeIndexedPath(evt.OldPath);
        var newPath = NormalizeIndexedPath(evt.FullPath);
        _pathToNode.TryGetValue(oldPath, out var existingNode);
        var persistedOldPath = existingNode?.FullPath ?? oldPath;
        var wasDirectory = ResolveIsDirectory(persistedOldPath, existingNode, evt.IsDirectory);

        DeletePersistedPath(persistedOldPath, wasDirectory);
        RemoveFromIndex(persistedOldPath);

        if (!Directory.Exists(newPath) && !File.Exists(newPath))
        {
            return;
        }

        AddPathToIndex(newPath, wasDirectory);
    }

    private void HandleModified(FileChangeEvent evt)
    {
        var path = NormalizeIndexedPath(evt.FullPath);
        if (_pathToNode.TryGetValue(path, out var node) && node.Metadata != null)
        {
            try
            {
                var fi = new FileInfo(path);
                node.Metadata.SizeBytes = fi.Length;
                node.Metadata.LastWriteTime = fi.LastWriteTime;

                var existing = _db.GetFileByPath(path);
                if (existing != null)
                {
                    existing.LastWriteTimeUtc = fi.LastWriteTimeUtc.Ticks;
                    existing.SizeBytes = fi.Length;
                    existing.LastIndexedTimeUtc = DateTime.UtcNow.Ticks;
                    _db.InsertFile(existing);
                }
            }
            catch { }
        }
    }

    #endregion

    #region Index Helpers

    private void IndexNode(FileSystemNode node)
    {
        foreach (var token in _tokenizer.Tokenize(node.Name))
        {
            _invertedIndex.Add(token, node);
        }
    }

    private void ResetInMemoryIndex()
    {
        _invertedIndex.Clear();
        _metadataMap.Clear();
        _pathToNode.Clear();
        _rootNode = null;
    }

    private void AddDirectoryTreeToIndex(
        string directoryPath,
        FileSystemNode parentNode,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath)) return;

        var parentDir = _db.GetDirectoryByPath(parentNode.FullPath);
        if (parentDir == null)
        {
            NotifyError($"Cannot index directory because its parent is missing from the database: {directoryPath}");
            return;
        }

        var directoryInfo = new DirectoryInfo(directoryPath);
        var node = new FileSystemNode(directoryInfo.Name, directoryPath, true);

        using var transaction = _db.BeginTransaction();
        try
        {
            var indexedDir = new IndexedDirectory
            {
                FullPath = directoryPath,
                Name = directoryInfo.Name,
                ParentId = parentDir.Id,
                Depth = parentDir.Depth + 1,
                LastWriteTimeUtc = directoryInfo.LastWriteTimeUtc.Ticks,
                LastIndexedTimeUtc = DateTime.UtcNow.Ticks,
                IsHidden = (directoryInfo.Attributes & FileAttributes.Hidden) != 0
            };
            var directoryId = _db.InsertDirectory(indexedDir);

            parentNode.AddChild(node);
            _pathToNode[directoryPath] = node;
            IndexNode(node);

            var processedItems = 0;
            ScanDirectoryRecursive(
                directoryPath,
                node,
                directoryId,
                indexedDir.Depth + 1,
                ref processedItems,
                totalItems: 1,
                ct,
                reportProgress: false);

            transaction.Commit();
        }
        catch
        {
            transaction.Rollback();
            RemoveFromIndex(directoryPath);
            throw;
        }
    }

    private bool ResolveIsDirectory(string path, FileSystemNode? existingNode, bool fallback)
    {
        if (existingNode != null) return existingNode.IsDirectory;
        if (_db.GetDirectoryByPath(path) != null) return true;
        if (_db.GetFileByPath(path) != null) return false;
        return fallback;
    }

    private void DeletePersistedPath(string path, bool isDirectory)
    {
        if (isDirectory)
        {
            _db.DeleteDirectory(path);
        }
        else
        {
            _db.DeleteFile(path);
        }
    }

    private void AddFileToIndex(string filePath, FileSystemNode parentNode)
    {
        if (_pathToNode.ContainsKey(filePath) || !File.Exists(filePath)) return;

        try
        {
            var fi = new FileInfo(filePath);
            var parentDir = _db.GetDirectoryByPath(parentNode.FullPath);
            if (parentDir == null)
            {
                NotifyError($"Cannot index file because its parent is missing from the database: {filePath}");
                return;
            }

            var node = new FileSystemNode(fi.Name, filePath, false)
            {
                Metadata = new FileMetadata
                {
                    SizeBytes = fi.Length,
                    CreatedTime = fi.CreationTime,
                    LastWriteTime = fi.LastWriteTime
                }
            };

            var indexedFile = new IndexedFile
            {
                FullPath = filePath,
                FileName = fi.Name,
                Extension = fi.Extension.ToLowerInvariant(),
                DirectoryId = parentDir.Id,
                SizeBytes = fi.Length,
                CreatedTimeUtc = fi.CreationTimeUtc.Ticks,
                LastWriteTimeUtc = fi.LastWriteTimeUtc.Ticks,
                LastIndexedTimeUtc = DateTime.UtcNow.Ticks
            };
            _db.InsertFile(indexedFile);

            parentNode.AddChild(node);
            _pathToNode[filePath] = node;
            _metadataMap[filePath] = node.Metadata!;
            IndexNode(node);
        }
        catch (Exception ex)
        {
            NotifyError($"Error indexing file {filePath}: {ex.Message}");
        }
    }

    private void RemoveFromIndex(string path)
    {
        path = NormalizeIndexedPath(path);
        if (!_pathToNode.TryGetValue(path, out var rootNode)) return;

        var normalizedRoot = NormalizeIndexedPath(rootNode.FullPath);
        var descendantPrefix = normalizedRoot.EndsWith(Path.DirectorySeparatorChar)
            ? normalizedRoot
            : normalizedRoot + Path.DirectorySeparatorChar;

        var nodesToRemove = _pathToNode.Values
            .Where(node =>
                ReferenceEquals(node, rootNode) ||
                NormalizeIndexedPath(node.FullPath)
                    .StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(node => node.FullPath.Length)
            .ToList();

        foreach (var node in nodesToRemove)
        {
            _invertedIndex.RemoveByPath(node.FullPath);
            node.Parent?.RemoveChild(node.FullPath);
            _pathToNode.Remove(node.FullPath);
            _metadataMap.Remove(node.FullPath);
        }
    }

    #endregion

    #region Progress Reporting

    private void ReportProgress(string status, int percentage, int itemCount, long elapsedMs)
    {
        var progress = new IndexProgress
        {
            Status = status,
            Percentage = percentage,
            ItemCount = itemCount,
            ElapsedMs = elapsedMs
        };
        QueueNotification(() => OnProgress?.Invoke(progress));
    }

    #endregion

    #region Public API

    public FileSystemNode? GetNode(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalizedPath = NormalizeIndexedPath(path);
        lock (_lock)
        {
            return _pathToNode.TryGetValue(normalizedPath, out var node) ? node : null;
        }
    }

    public SearchSnapshot CreateSearchSnapshot(
        CancellationToken cancellationToken = default)
    {
        lock (_lock)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            cancellationToken.ThrowIfCancellationRequested();
            return SearchSnapshot.Create(
                _invertedIndex.CreateSnapshot(cancellationToken),
                _pathToNode.Values,
                _rootNode,
                cancellationToken);
        }
    }

    public void IncrementOpenCount(string path)
    {
        lock (_lock)
        {
            var normalizedPath = NormalizeIndexedPath(path);
            _db.IncrementOpenCount(normalizedPath);

            if (_metadataMap.TryGetValue(normalizedPath, out var meta))
            {
                meta.OpenCount++;
            }
        }
    }

    private static FileMetadata CloneMetadata(FileMetadata metadata) =>
        new()
        {
            SizeBytes = metadata.SizeBytes,
            CreatedTime = metadata.CreatedTime,
            LastWriteTime = metadata.LastWriteTime,
            OpenCount = metadata.OpenCount
        };

    public IndexStats GetStats()
    {
        return new IndexStats
        {
            FileCount = _db.GetFileCount(),
            DirectoryCount = _db.GetDirectoryCount(),
            TokenCount = _invertedIndex.TokenCount,
            DatabasePath = _db.DatabasePath,
            LastScanTime = GetLastScanTime()
        };
    }

    private DateTime? GetLastScanTime()
    {
        var ticks = _db.GetMetadata(IndexMetadata.Keys.LastFullScanTime);
        if (long.TryParse(ticks, out var t))
        {
            return new DateTime(t, DateTimeKind.Utc).ToLocalTime();
        }
        return null;
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        _lifecycleGate.Wait();
        try
        {
            if (_disposed) return;

            _disposed = true;
            try
            {
                _watcher.Stop();
            }
            finally
            {
                try
                {
                    StopBackgroundSyncAsync().GetAwaiter().GetResult();
                }
                finally
                {
                    _reconciliationGate.Wait();
                    try
                    {
                        _watcher.Dispose();
                    }
                    finally
                    {
                        try
                        {
                            _db.Dispose();
                        }
                        finally
                        {
                            _reconciliationGate.Release();
                        }
                    }
                }
            }
        }
        finally
        {
            _lifecycleGate.Release();
        }

        GC.SuppressFinalize(this);
    }

    private static string NormalizeIndexedPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return path;

        var fullPath = Path.GetFullPath(
            path.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar));
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void NotifyDeltaSyncProgress(int processed, int total, int percentage)
    {
        QueueNotification(() => OnDeltaSyncProgress?.Invoke(processed, total, percentage));
    }

    private void NotifyError(string message)
    {
        QueueNotification(() => OnError?.Invoke(message));
    }

    private void QueueNotification(Action notification)
    {
        lock (_notificationLock)
        {
            if (_disposed) return;

            _notificationTask = _notificationTask.ContinueWith(
                _ =>
                {
                    if (_disposed) return;
                    try { notification(); }
                    catch { }
                },
                CancellationToken.None,
                TaskContinuationOptions.None,
                TaskScheduler.Default);
        }
    }

    #endregion
}

public class IndexProgress
{
    public string Status { get; set; } = string.Empty;
    public int Percentage { get; set; }
    public int ItemCount { get; set; }
    public long ElapsedMs { get; set; }
}

public class IndexStats
{
    public int FileCount { get; set; }
    public int DirectoryCount { get; set; }
    public int TokenCount { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public DateTime? LastScanTime { get; set; }
}
