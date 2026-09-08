using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.IO;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager : IDisposable
{
    private readonly IndexDatabase _db;
    private readonly FileWatcherService _watcher;
    private readonly ITokenizer _tokenizer;
    private readonly FileSystemPathGuard? _measurementPathGuard;
    private readonly bool _enforceMeasurementPathSafety;
    private readonly bool _skipReparsePoints;
    private readonly object _lock = new();
    private readonly object _notificationLock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private readonly SemaphoreSlim _reconciliationGate = new(1, 1);
    private readonly SemaphoreSlim _reconciliationSignal = new(0, 1);
    private readonly TimeSpan _reconciliationInterval;
    private readonly TimeSpan _watcherRevivalDelay;
    private Task _notificationTask = Task.CompletedTask;
    private CancellationTokenSource? _backgroundSyncCts;
    private Task? _backgroundSyncTask;
    
    private FileSystemNode? _rootNode;
    private readonly Dictionary<string, FileSystemNode> _pathToNode;
    private ISearchStateReader _publishedSearchState = SearchState.Empty;
    private readonly SearchStateLayout _layout;
    private HashSet<string>? _reconciliationChangedPaths;
    private long _incrementalReconciliationPublishCount;
    
    private volatile bool _disposed;
    private bool _isInitialized;
    
    private volatile bool _isDeltaSyncRunning = false;
    private volatile int _deltaSyncProgress = 0;
    private volatile int _deltaSyncTotal = 0;
    private volatile int _deltaSyncProcessed = 0;
    private IReadOnlyList<string> _activeRootPaths = Array.Empty<string>();
    private long _reconciliationRunCount;
    private bool _changeFeedCoversDowntime;
    private volatile bool _changeFeedGuarding;
    private int _detachedNodeCount;
    private int _watcherRevivalInFlight;
    private long _subtreeNodesInspected;
    private long _lastReconciliationAtTicks;
    private long _lastReconciliationDurationTicks;
    private long _lastReconciliationScanDurationTicks;
    private int _lastReconciliationChanges;
    private int _lastReconciliationRepublished;
    private long _republishCount;
    private long _lastRepublishAtTicks;
    private long _lastRepublishDurationTicks;

    public event Action<IndexProgress>? OnProgress;

    public event Action<FileChangeEvent>? OnFileChange;

    public event Action<string>? OnError;

    public event Action? OnWatcherFault;
    
    public event Action<int, int, int>? OnDeltaSyncProgress;

    public event Action<bool>? OnDeltaSyncStateChanged;

    public IndexManager(ITokenizer? tokenizer = null)
        : this(new IndexDatabase(), new FileWatcherService(), tokenizer)
    {
    }

    public IndexManager(ITokenizer? tokenizer, SearchStateLayout layout)
        : this(new IndexDatabase(), new FileWatcherService(), tokenizer, layout: layout)
    {
    }

    public static IndexManager CreateWithDatabasePath(
        string databasePath,
        ITokenizer? tokenizer = null,
        bool enforceMeasurementPathSafety = true,
        bool skipReparsePoints = false,
        SearchStateLayout layout = SearchStateLayout.Legacy)
    {
        if (string.IsNullOrWhiteSpace(databasePath))
            throw new ArgumentException("Database yolu boş olamaz.", nameof(databasePath));

        return new IndexManager(
            new IndexDatabase(databasePath),
            new FileWatcherService(skipReparsePoints: skipReparsePoints),
            tokenizer,
            measurementPathGuard: enforceMeasurementPathSafety
                || skipReparsePoints
                ? FileSystemPathGuard.Default
                : null,
            enforceMeasurementPathSafety: enforceMeasurementPathSafety,
            skipReparsePoints: skipReparsePoints,
            layout: layout);
    }

    internal IndexManager(
        IndexDatabase database,
        FileWatcherService watcher,
        ITokenizer? tokenizer = null,
        TimeSpan? reconciliationInterval = null,
        FileSystemPathGuard? measurementPathGuard = null,
        bool enforceMeasurementPathSafety = false,
        bool skipReparsePoints = false,
        TimeSpan? watcherRevivalDelay = null,
        SearchStateLayout layout = SearchStateLayout.Legacy)
    {
        _tokenizer = tokenizer ?? new BasicTokenizer();
        _layout = layout;
        _publishedSearchState = ISearchStateReader.Empty(layout);
        _db = database;
        _watcher = watcher;
        _measurementPathGuard = measurementPathGuard;
        _enforceMeasurementPathSafety =
            enforceMeasurementPathSafety || measurementPathGuard != null;
        _skipReparsePoints = skipReparsePoints || measurementPathGuard != null;
        _reconciliationInterval = reconciliationInterval ?? TimeSpan.FromMinutes(10);
        if (_reconciliationInterval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(reconciliationInterval));
        _watcherRevivalDelay = watcherRevivalDelay ?? TimeSpan.FromSeconds(2);
        if (_watcherRevivalDelay <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(watcherRevivalDelay));
        _pathToNode = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);

        _watcher.OnChange += HandleFileChange;
        _watcher.OnError += HandleWatcherError;
        _watcher.OnFault += HandleWatcherFault;
    }

    #region Properties

    public ISearchStateReader CurrentSearchState => Volatile.Read(ref _publishedSearchState);
    internal long IncrementalReconciliationPublishCount => Interlocked.Read(ref _incrementalReconciliationPublishCount);
    internal IReadOnlyList<KeyValuePair<string, FileSystemNode>> IndexedEntries {
        get {
            lock (_lock) {
                if (UsesCompactCatalog) return CreateCompactNodeProjection().Nodes.ToList();
                return _pathToNode.ToList();
            }
        }
    }
    internal IReadOnlyDictionary<string, FileMetadata> MetadataMap {
        get {
            lock (_lock) {
                if (UsesCompactCatalog) return CurrentSearchState.GetAllItems()
                    .Where(item => !item.IsDirectory).ToDictionary(
                        item => item.FullPath, CreateCompactMetadata, StringComparer.OrdinalIgnoreCase);
                return _pathToNode.Where(entry =>
                    !entry.Value.IsDirectory && entry.Value.Metadata != null).ToDictionary(
                    entry => entry.Key,
                    entry => CloneMetadata(entry.Value.Metadata!),
                    StringComparer.OrdinalIgnoreCase);
            }
        }
    }
    public FileSystemNode? RootNode {
        get {
            lock (_lock) {
                if (UsesCompactCatalog) return CreateCompactNodeProjection().Root;
                return _rootNode;
            }
        }
    }
    public bool IsInitialized => _isInitialized;

    public bool IsWatching => _watcher.IsWatching;
    public string DatabasePath => _db.DatabasePath;

    internal int IndexedFileCount
    {
        get
        {
            lock (_lock)
            {
                if (UsesCompactCatalog) return CurrentSearchState.ItemCount + (_compactSingleRootPath is null ? 0 : 1);
                return _pathToNode.Count;
            }
        }
    }

    public bool IsDeltaSyncRunning => _isDeltaSyncRunning;
    public int DeltaSyncProgress => _deltaSyncProgress;
    public int DeltaSyncProcessed => _deltaSyncProcessed;
    public int DeltaSyncTotal => _deltaSyncTotal;
    internal long ReconciliationRunCount => Interlocked.Read(ref _reconciliationRunCount);

    internal bool ChangeFeedCoversDowntime => _changeFeedCoversDowntime;

    internal bool ChangeFeedGuarding => _changeFeedGuarding;

    internal void NoteChangeFeedCoverage(bool coversDowntime)
    {
        _changeFeedCoversDowntime = coversDowntime;
        _changeFeedGuarding = coversDowntime;
    }

    internal void NoteChangeFeedLost()
    {
        _changeFeedGuarding = false;
        RequestReconciliation();
    }

    internal TimeSpan NextReconciliationDelay() =>
        _changeFeedGuarding ? Timeout.InfiniteTimeSpan : _reconciliationInterval;

    public IndexDiagnosticsReport GetDiagnosticsReport()
    {
        var reconciliationAt = Interlocked.Read(ref _lastReconciliationAtTicks);
        var republishAt = Interlocked.Read(ref _lastRepublishAtTicks);

        return new IndexDiagnosticsReport(
            Interlocked.Read(ref _reconciliationRunCount),
            reconciliationAt == 0 ? null : new DateTime(reconciliationAt),
            TimeSpan.FromTicks(Interlocked.Read(ref _lastReconciliationDurationTicks)),
            TimeSpan.FromTicks(Interlocked.Read(ref _lastReconciliationScanDurationTicks)),
            Interlocked.CompareExchange(ref _lastReconciliationChanges, 0, 0),
            Interlocked.CompareExchange(ref _lastReconciliationRepublished, 0, 0) != 0,
            Interlocked.Read(ref _republishCount),
            republishAt == 0 ? null : new DateTime(republishAt),
            TimeSpan.FromTicks(Interlocked.Read(ref _lastRepublishDurationTicks)),
            CurrentSearchState.ItemCount);
    }

    internal Task QueuedNotifications
    {
        get
        {
            lock (_notificationLock)
            {
                return _notificationTask;
            }
        }
    }

    #endregion

    #region Initialization

    public async Task InitializeAsync(
        IEnumerable<string> rootPaths,
        CancellationToken ct = default,
        Func<IReadOnlyList<string>, CancellationToken, Task>? beforeWatching = null)
    {
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? hook = null;
        if (beforeWatching is not null)
        {
            hook = async (paths, cancellationToken) =>
            {
                await beforeWatching(paths, cancellationToken).ConfigureAwait(false);
                return false;
            };
        }

        await InitializeWithWatcherFenceAsync(rootPaths, ct, hook).ConfigureAwait(false);
    }

    internal Task InitializeWithWatcherFenceAsync(
        IEnumerable<string> rootPaths,
        CancellationToken ct,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? beforeWatcherActivation)
        => InitializeWithWatcherFenceCoreAsync(rootPaths, ct, beforeWatcherActivation);

    private async Task InitializeWithWatcherFenceCoreAsync(
        IEnumerable<string> rootPaths,
        CancellationToken ct,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? beforeWatcherActivation)
    {
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await InitializeCoreAsync(rootPaths, ct, beforeWatcherActivation);
        }
        finally
        {
            _lifecycleGate.Release();
        }
    }

    private async Task InitializeCoreAsync(
        IEnumerable<string> rootPaths,
        CancellationToken ct,
        Func<IReadOnlyList<string>, CancellationToken, Task<bool>>? beforeWatcherActivation = null)
    {
        await StopBackgroundSyncAsync();
        _watcher.Stop();
        _watcher.ClearWatches();

        var sw = Stopwatch.StartNew();
        var paths = NormalizeRootPaths(rootPaths);
        
        ReportProgress("Başlatılıyor...", 0, 0, 0);

        var newRootsKey = string.Join("|", paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        bool hasCache;
        lock (_lock)
        {
            _db.Open();

            var cachedRoot = _db.GetMetadata(IndexMetadata.Keys.ScanRootPath);
            hasCache = cachedRoot != null &&
                       cachedRoot.Equals(newRootsKey, StringComparison.OrdinalIgnoreCase) &&
                       _db.GetFileCount() > 0;
        }

        var loadedFromCache = false;
        if (hasCache)
        {
            ReportProgress("Önbellekten yükleniyor...", 0, 0, 0);
            loadedFromCache = await LoadFromCacheMultiAsync(paths, ct);
        }

        if (!loadedFromCache)
        {
            ReportProgress("İlk kurulum - dosyalar taranıyor...", 0, 0, 0);
            await BootstrapScanMultiAsync(paths, ct);
        }

        ReportProgress(
            "Arama hazırlanıyor...",
            0,
            IndexedFileCount,
            sw.ElapsedMilliseconds,
            isIndeterminate: true);
        await Task.Run(() =>
        {
            ct.ThrowIfCancellationRequested();
            lock (_lock)
            {
                PublishSearchStateFromCurrentIndex();
            }
        }, ct).ConfigureAwait(false);

        ReleaseCompactStartupWorkspace();
        _activeRootPaths = paths;
        _isInitialized = true;

        var watcherPrepared = false;
        if (beforeWatcherActivation is not null)
        {
            try
            {
                watcherPrepared = await beforeWatcherActivation(paths, ct).ConfigureAwait(false);
            }
            catch
            {
                _watcher.Stop();
                _watcher.ClearWatches();
                _isInitialized = false;
                throw;
            }
        }

        if (watcherPrepared)
        {
            _watcher.ResumeDispatch();
        }
        else
        {
            SetupWatchers(paths);
        }

        sw.Stop();
        lock (_lock)
        {
            _db.SetMetadata(IndexMetadata.Keys.LastBuildDurationMs, sw.ElapsedMilliseconds.ToString());
            _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, newRootsKey);
        }

        StartBackgroundReconciliation(paths);
        ReportProgress("Hazır", 100, IndexedFileCount, sw.ElapsedMilliseconds);
    }

    public async Task InitializeAsync(string rootPath, CancellationToken ct = default)
    {
        await InitializeAsync(new[] { rootPath }, ct);
    }

    public async Task RescanAsync(string rootPath, CancellationToken ct = default)
    {
        if (UsesCompactCatalog)
        {
            await RescanCompactAsync(rootPath, ct).ConfigureAwait(false);
            return;
        }
        await _lifecycleGate.WaitAsync(ct);
        try
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            await StopBackgroundSyncAsync();
            _watcher.Stop();
            _watcher.ClearWatches();
            lock (_lock)
            {
                _db.ClearIndex();
                ResetInMemoryIndex();
            }

            var normalizedRootPath = NormalizeIndexedPath(rootPath);
            await BootstrapScanAsync(normalizedRootPath, ct);
            var paths = new List<string> { normalizedRootPath };
            _activeRootPaths = paths;
            lock (_lock)
            {
                PublishSearchStateFromCurrentIndex();
            }
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
        if (UsesCompactCatalog)
        {
            await BootstrapCompactScanAsync(rootPath, ct).ConfigureAwait(false);
            return;
        }
        EnsureMeasurementDirectorySafe(rootPath);
        var sw = Stopwatch.StartNew();

        var rootName = Path.GetFileName(rootPath);
        if (string.IsNullOrEmpty(rootName)) rootName = rootPath;

        lock (_lock)
        {
            _db.ClearIndex();
            ResetInMemoryIndex();
            _rootNode = new FileSystemNode(rootName, rootPath, true);
            _pathToNode[rootPath] = _rootNode;
        }

        int totalItems = 0;
        int processedItems = 0;

        await Task.Run(() =>
        {
            lock (_lock)
            {
            EnsureMeasurementDirectorySafe(rootPath);
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
                EnsureMeasurementDirectorySafe(rootPath);
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
            }
        }, ct);

        lock (_lock)
        {
            _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, rootPath);
            _db.SetMetadata(IndexMetadata.Keys.LastFullScanTime, DateTime.UtcNow.Ticks.ToString());
            _db.SetMetadata(IndexMetadata.Keys.TotalFilesIndexed, IndexedFileCount.ToString());
        }

        sw.Stop();
        ReportProgress("Tarama tamamlandı", 100, IndexedFileCount, sw.ElapsedMilliseconds);
    }

    private async Task BootstrapScanMultiAsync(List<string> rootPaths, CancellationToken ct)
    {
        if (UsesCompactCatalog)
        {
            await BootstrapCompactScanMultiAsync(rootPaths, ct).ConfigureAwait(false);
            return;
        }
        foreach (var rootPath in rootPaths)
        {
            EnsureMeasurementDirectorySafe(rootPath);
        }

        var sw = Stopwatch.StartNew();

        lock (_lock)
        {
            _db.ClearIndex();
            ResetInMemoryIndex();
            _rootNode = new FileSystemNode("Root", "", true);
        }

        int totalItems = 0;
        int processedItems = 0;

        await Task.Run(() =>
        {
            lock (_lock)
            {
            foreach (var rootPath in rootPaths)
            {
                if (!Directory.Exists(rootPath)) continue;
                EnsureMeasurementDirectorySafe(rootPath);
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
                    EnsureMeasurementDirectorySafe(rootPath);
                    
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
            }
        }, ct);

        var rootsKey = string.Join("|", rootPaths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        lock (_lock)
        {
            _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, rootsKey);
            _db.SetMetadata(IndexMetadata.Keys.LastFullScanTime, DateTime.UtcNow.Ticks.ToString());
            _db.SetMetadata(IndexMetadata.Keys.TotalFilesIndexed, IndexedFileCount.ToString());
        }

        sw.Stop();
        ReportProgress("Tarama tamamlandı", 100, IndexedFileCount, sw.ElapsedMilliseconds);
    }

    private bool ScanDirectoryRecursive(string path, FileSystemNode parentNode, long parentDirId,
                                        int depth, ref int processedItems, int totalItems, CancellationToken ct,
                                        bool reportProgress = true)
    {
        ct.ThrowIfCancellationRequested();
        EnsureMeasurementDirectorySafe(path);
        var complete = true;

        try
        {
            foreach (var dir in Directory.GetDirectories(path))
            {
                ct.ThrowIfCancellationRequested();

                if (ShouldSkipReparsePath(dir))
                    continue;

                var dirName = Path.GetFileName(dir);
                var dirInfo = new DirectoryInfo(dir);

                if ((dirInfo.Attributes & FileAttributes.Hidden) != 0 ||
                    (dirInfo.Attributes & FileAttributes.System) != 0)
                    continue;

                var dirNode = new FileSystemNode(dirName, dir, true);
                parentNode.AddChild(dirNode);
                _pathToNode[dir] = dirNode;
                _reconciliationChangedPaths?.Add(dir);


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

                complete &= ScanDirectoryRecursive(
                    dir,
                    dirNode,
                    dirId,
                    depth + 1,
                    ref processedItems,
                    totalItems,
                    ct,
                    reportProgress);
            }

            EnsureMeasurementDirectorySafe(path);
            foreach (var file in Directory.GetFiles(path))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    if (ShouldSkipReparsePath(file))
                        continue;

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
                    _reconciliationChangedPaths?.Add(file);

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
                catch (UnauthorizedAccessException) { complete = false; }
                catch (IOException) { complete = false; }
            }
        }
        catch (UnauthorizedAccessException) { complete = false; }
        catch (IOException) { complete = false; }

        return complete;
    }

    private int CountItems(string path)
    {
        EnsureMeasurementDirectorySafe(path);
        int count = 0;
        try
        {
            foreach (var file in Directory.GetFiles(path))
            {
                if (!ShouldSkipReparsePath(file))
                    count++;
            }

            foreach (var dir in Directory.GetDirectories(path))
            {
                if (ShouldSkipReparsePath(dir))
                    continue;

                count++;
                count += CountItems(dir);
            }
        }
        catch { }
        return count;
    }

    #endregion

    #region Cache Loading

    private async Task<bool> LoadFromCacheMultiAsync(List<string> rootPaths, CancellationToken ct)
    {
        if (UsesCompactCatalog) return await LoadCompactFromCacheMultiAsync(rootPaths, ct).ConfigureAwait(false);
        var sw = Stopwatch.StartNew();

        lock (_lock)
        {
            ResetInMemoryIndex();
        }

        var accepted = await Task.Run(() =>
        {
            lock (_lock)
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

                if (!IsCanonicalIndexedPath(dir.FullPath)) return false;

                if (rootPathNodes.TryGetValue(dir.FullPath, out var existingRootNode))
                {
                    dirMap[dir.Id] = (dir, existingRootNode);
                    continue;
                }

                var node = new FileSystemNode(dir.Name, dir.FullPath, true);
                _pathToNode[dir.FullPath] = node;
                dirMap[dir.Id] = (dir, node);
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

                    _detachedNodeCount++;
                }
            }

            int fileCount = 0;
            int totalFiles = _db.GetFileCount();

            foreach (var file in _db.GetAllFiles())
            {
                ct.ThrowIfCancellationRequested();

                if (!IsCanonicalIndexedPath(file.FullPath)) return false;

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
                    else
                    {
                        var attached = false;
                        if (parentPath != null)
                        {
                            foreach (var rootPath in rootPathNodes.Keys)
                            {
                                if (string.Equals(parentPath, rootPath, StringComparison.OrdinalIgnoreCase))
                                {
                                    rootPathNodes[rootPath].AddChild(node);
                                    attached = true;
                                    break;
                                }
                            }
                        }

                        if (!attached)
                        {
                            _detachedNodeCount++;
                        }
                    }
                }


                fileCount++;
                if (fileCount % 100 == 0)
                {
                    int pct = (int)(fileCount * 100.0 / totalFiles);
                    ReportProgress($"Önbellek yükleniyor: {fileCount}/{totalFiles}", pct, fileCount, 0);
                }
            }

            return true;
            }
        }, ct);

        sw.Stop();

        if (!accepted)
        {
            lock (_lock)
            {
                ResetInMemoryIndex();
            }

            return false;
        }

        ReportProgress("Önbellek yüklendi", 100, IndexedFileCount, sw.ElapsedMilliseconds);
        return true;
    }

    #endregion

    #region Reconciliation

    private void StartBackgroundReconciliation(List<string> rootPaths)
    {
        while (_reconciliationSignal.Wait(0)) { }

        var skipStartupPass = _changeFeedCoversDowntime;
        _changeFeedCoversDowntime = false;
        var syncCts = new CancellationTokenSource();
        _backgroundSyncCts = syncCts;
        _backgroundSyncTask = Task.Run(
            () => BackgroundReconciliationLoopAsync(rootPaths, skipStartupPass, syncCts.Token),
            syncCts.Token);
    }

    private async Task BackgroundReconciliationLoopAsync(
        IReadOnlyList<string> rootPaths,
        bool skipStartupPass,
        CancellationToken ct)
    {
        var skipping = skipStartupPass;

        while (!ct.IsCancellationRequested)
        {
            try
            {
                if (skipping)
                {
                    skipping = false;
                }
                else
                {
                    await ReconcilePathsAsync(rootPaths, ct).ConfigureAwait(false);
                }
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
                    .WaitAsync(NextReconciliationDelay(), ct)
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

    public void NotifyExternalError(string message) => NotifyError(message);

    public bool ApplyExternalChanges(IReadOnlyList<FileChangeEvent> changes)
    {
        ArgumentNullException.ThrowIfNull(changes);

        if (_disposed || !_isInitialized)
        {
            return false;
        }

        var applied = true;
        foreach (var change in changes)
        {
            applied &= TryHandleFileChange(change);
        }

        return applied;
    }

    internal async Task<bool> ReconcileWithinLifecycleAsync(
        string path,
        CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path) || _disposed || !_isInitialized)
        {
            return false;
        }

        try
        {
            return await ReconcilePathsAsync(
                    new[] { NormalizeIndexedPath(path) },
                    ct)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            NotifyError($"Startup reconciliation error for {path}: {ex.Message}");
            return false;
        }
    }

    internal bool BeginWatcherCaptureWithinLifecycle(IReadOnlyList<string> rootPaths)
    {
        ArgumentNullException.ThrowIfNull(rootPaths);

        if (_disposed || !_isInitialized || _watcher.IsWatching)
        {
            return false;
        }

        return SetupWatchers(rootPaths, dispatchPaused: true);
    }

    public async Task<bool> EnsureSyncedAsync(string path, CancellationToken ct = default)
    {
        if (string.IsNullOrWhiteSpace(path))
            return false;

        bool enteredLifecycle;
        try
        {
            enteredLifecycle = await _lifecycleGate.WaitAsync(0, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            return false;
        }

        if (!enteredLifecycle)
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
        finally
        {
            _lifecycleGate.Release();
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
            NotifyDeltaSyncStateChanged(isRunning: true);

            var startedAt = DateTime.Now;
            var runTimestamp = Stopwatch.GetTimestamp();

            var snapshot = await Task.Run(
                    () => CaptureDiskSnapshot(normalizedRoots, ct),
                    ct)
                .ConfigureAwait(false);

            var scanElapsed = Stopwatch.GetElapsedTime(runTimestamp);

            _deltaSyncTotal = snapshot.Entries.Count;
            var changes = ApplyReconciliationSnapshot(
                normalizedRoots,
                snapshot,
                ct);

            _deltaSyncProcessed = _deltaSyncTotal;
            _deltaSyncProgress = 100;
            Interlocked.Increment(ref _reconciliationRunCount);
            Interlocked.Exchange(ref _lastReconciliationChanges, changes);
            Interlocked.Exchange(ref _lastReconciliationRepublished, 0);
            Interlocked.Exchange(ref _lastReconciliationAtTicks, startedAt.Ticks);
            Interlocked.Exchange(
                ref _lastReconciliationScanDurationTicks,
                scanElapsed.Ticks);
            Interlocked.Exchange(
                ref _lastReconciliationDurationTicks,
                Stopwatch.GetElapsedTime(runTimestamp).Ticks);
            NotifyDeltaSyncProgress(
                _deltaSyncProcessed,
                _deltaSyncTotal,
                _deltaSyncProgress);

            if (changes > 0)
            {
                Interlocked.Exchange(ref _lastReconciliationRepublished, 1);
                ReportProgress(
                    $"İndeks uzlaştırıldı: {changes} değişiklik.",
                    100,
                    IndexedFileCount,
                    0);
            }

            if (snapshot.Errors.Count > 0)
            {
                NotifyError(
                    $"Reconciliation skipped {snapshot.ProtectedScopes.Count} scope(s), " +
                    $"{snapshot.UnreadableScopes.Count} of them unreadable: " +
                    string.Join(" | ", snapshot.Errors.Take(3)));
            }

            return snapshot.UnreadableScopes.Count == 0;
        }
        finally
        {
            _isDeltaSyncRunning = false;
            NotifyDeltaSyncStateChanged(isRunning: false);
            _reconciliationGate.Release();
        }
    }

    private int ApplyReconciliationSnapshot(
        IReadOnlyList<string> rootPaths,
        ReconciliationSnapshot snapshot,
        CancellationToken ct)
    {
        if (UsesCompactCatalog) return ApplyCompactReconciliationSnapshot(rootPaths, snapshot, ct);
        try
        {
            lock (_lock)
            {
                _reconciliationChangedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                try
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
                                var updated = UpdatePersistedDirectory(existing, entry);
                                changes += updated;
                                if (updated > 0)
                                    _reconciliationChangedPaths.Add(entry.Path);
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

                        var fileUpdated = UpdatePersistedFile(existing, entry);
                        changes += fileUpdated;
                        if (fileUpdated > 0)
                            _reconciliationChangedPaths.Add(entry.Path);
                    }
                    if (changes > 0)
                        PublishReconciliationChanges(_reconciliationChangedPaths);
                    return changes;
                }
                finally
                {
                    _reconciliationChangedPaths = null;
                }
            }
        }
        catch
        {
            lock (_lock)
            {
                PublishSearchStateFromCurrentIndex();
            }
            throw;
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
        if (_pathToNode.ContainsKey(rootPath) ||
            !Directory.Exists(rootPath) ||
            ShouldSkipReparsePath(rootPath))
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
            _reconciliationChangedPaths?.Add(rootPath);

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

    private ReconciliationSnapshot CaptureDiskSnapshot(
        IReadOnlyList<string> rootPaths,
        CancellationToken ct,
        bool followReparsePoints = false)
    {
        var snapshot = new ReconciliationSnapshot();

        foreach (var rootPath in rootPaths)
        {
            ct.ThrowIfCancellationRequested();

            if (Directory.Exists(rootPath))
            {
                CaptureDirectoryTree(rootPath, snapshot, ct, followReparsePoints);
            }
            else if (File.Exists(rootPath))
            {
                CaptureFile(rootPath, snapshot);
            }
        }

        return snapshot;
    }

    private void CaptureDirectoryTree(
        string rootPath,
        ReconciliationSnapshot snapshot,
        CancellationToken ct,
        bool followReparsePoints = false)
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
                if (ShouldSkipReparsePath(directoryPath))
                {
                    snapshot.ProtectedScopes.Add(directoryPath);
                    snapshot.Errors.Add($"{directoryPath}: reparse point traversal skipped");
                    continue;
                }

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

                if (!followReparsePoints && (attributes & FileAttributes.ReparsePoint) != 0)
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
                    snapshot.UnreadableScopes.Add(directoryPath);
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
                    snapshot.UnreadableScopes.Add(directoryPath);
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
                    if (ShouldSkipReparsePath(path))
                    {
                        var normalizedPath = NormalizeIndexedPath(path);
                        snapshot.ProtectedScopes.Add(normalizedPath);
                        snapshot.Errors.Add($"{normalizedPath}: reparse point traversal skipped");
                        continue;
                    }

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
                        snapshot.UnreadableScopes.Add(normalizedPath);
                        snapshot.Errors.Add($"{normalizedPath}: {ex.Message}");
                    }
                }
            }
        }
    }

    private void CaptureFile(
        string filePath,
        ReconciliationSnapshot snapshot)
    {
        var normalizedPath = NormalizeIndexedPath(filePath);
        try
        {
            if (ShouldSkipReparsePath(normalizedPath))
            {
                snapshot.ProtectedScopes.Add(normalizedPath);
                snapshot.Errors.Add($"{normalizedPath}: reparse point traversal skipped");
                return;
            }

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

        public HashSet<string> UnreadableScopes { get; } =
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

    private void HandleWatcherFault(Exception exception)
    {
        NoteChangeFeedLost();
        NotifyWatcherFault();
        BeginWatcherRevival();
    }

    internal Task? WatcherRevival { get; private set; }

    private const int WatcherRevivalAttempts = 3;

    private void BeginWatcherRevival()
    {
        if (_disposed || !_isInitialized || _activeRootPaths.Count == 0)
        {
            return;
        }

        if (Interlocked.Exchange(ref _watcherRevivalInFlight, 1) != 0)
        {
            return;
        }

        var roots = _activeRootPaths;
        WatcherRevival = Task.Run(() => ReviveWatcherAsync(roots));
    }

    private async Task ReviveWatcherAsync(IReadOnlyList<string> roots)
    {
        try
        {
            for (var attempt = 0; attempt < WatcherRevivalAttempts; attempt++)
            {
                await Task.Delay(_watcherRevivalDelay).ConfigureAwait(false);

                if (_disposed)
                {
                    return;
                }

                var revived = false;
                try
                {
                    lock (_lock)
                    {
                        if (!_disposed)
                        {
                            revived = SetupWatchers(roots);
                        }
                    }
                }
                catch (Exception failure)
                {
                    NotifyError($"Watcher yeniden kurulamadı: {failure.Message}");
                }

                if (revived)
                {
                    NotifyError("Watcher yeniden kuruldu; canlı izleme sürüyor.");
                    RequestReconciliation();
                    return;
                }
            }

            NotifyError(
                "Watcher yeniden kurulamadı; indeks periyodik tam taramayla güncel tutulacak.");
        }
        finally
        {
            Interlocked.Exchange(ref _watcherRevivalInFlight, 0);
        }
    }

    private void NotifyWatcherFault()
    {
        try
        {
            OnWatcherFault?.Invoke();
        }
        catch
        {
        }
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

    private bool SetupWatchers(
        IEnumerable<string> rootPaths,
        bool dispatchPaused = false)
    {
        _watcher.Stop();
        _watcher.ClearWatches();

        var configured = false;
        var configuredRoots = new List<string>();
        var normalizedRoots = NormalizeRootPaths(rootPaths)
            .OrderBy(path => path.Length);

        foreach (var rootPath in normalizedRoots)
        {
            if (_enforceMeasurementPathSafety)
            {
                EnsureMeasurementDirectorySafe(rootPath);
            }
            else if (!Directory.Exists(rootPath) ||
                     ShouldSkipReparsePath(rootPath))
            {
                continue;
            }

            if (configuredRoots.Any(parent => IsSameOrDescendantPath(rootPath, parent)))
                continue;

            _watcher.Watch(rootPath);
            configuredRoots.Add(rootPath);
            configured = true;
        }

        if (configured)
        {
            _watcher.Start(dispatchPaused);
        }

        return configured;
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

    internal int DetachedNodeCount => _detachedNodeCount;

    internal long SubtreeNodesInspected => Interlocked.Read(ref _subtreeNodesInspected);

    private List<FileSystemNode> CollectIndexedSubtree(string normalizedPath)
    {
        return _detachedNodeCount == 0
            ? CollectSubtreeByWalk(normalizedPath)
            : CollectSubtreeByScan(normalizedPath);
    }

    internal List<FileSystemNode> CollectSubtreeByWalk(string normalizedPath)
    {
        if (UsesCompactCatalog) return CollectCompactSubtreeNodes(normalizedPath);
        var collected = new List<FileSystemNode>();
        if (!_pathToNode.TryGetValue(normalizedPath, out var start))
        {
            return collected;
        }

        var pending = new Stack<FileSystemNode>();
        pending.Push(start);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            collected.Add(current);
            Interlocked.Increment(ref _subtreeNodesInspected);

            foreach (var child in current.Children)
            {
                pending.Push(child);
            }
        }

        return collected;
    }

    internal List<FileSystemNode> CollectSubtreeByScan(string normalizedPath)
    {
        if (UsesCompactCatalog) return CollectCompactSubtreeNodes(normalizedPath);
        Interlocked.Add(ref _subtreeNodesInspected, _pathToNode.Count);

        return _pathToNode.Values
            .Where(node => IsSameOrDescendantPath(node.FullPath, normalizedPath))
            .ToList();
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

    private void HandleFileChange(FileChangeEvent evt) => TryHandleFileChange(evt);

    private bool TryHandleFileChange(FileChangeEvent evt)
    {
        if (UsesCompactCatalog) return TryHandleCompactFileChange(evt);
        string? error = null;
        var processed = false;

        lock (_lock)
        {
            try
            {
                var landed = evt.ChangeType switch
                {
                    FileChangeType.Created => HandleCreated(evt),
                    FileChangeType.Deleted => HandleDeleted(evt),
                    FileChangeType.Renamed => HandleRenamed(evt),
                    _ => HandleModified(evt)
                };

                PublishSearchStateForFileChange(evt);
                processed = landed;
            }
            catch (Exception ex)
            {
                error = $"Error handling {evt.ChangeType}: {ex.Message}";
                PublishSearchStateFromCurrentIndex();
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

        return processed;
    }

    private bool HandleCreated(FileChangeEvent evt)
    {
        var isDirectory = Directory.Exists(evt.FullPath);
        if (!isDirectory && !File.Exists(evt.FullPath))
        {
            isDirectory = evt.IsDirectory;
        }

        return AddPathToIndex(evt.FullPath, isDirectory);
    }

    private bool AddPathToIndex(
        string path,
        bool isDirectory,
        CancellationToken ct = default)
    {
        path = NormalizeIndexedPath(path);

        if (ShouldSkipReparsePath(path))
        {
            if (_pathToNode.TryGetValue(path, out var skippedNode))
            {
                DeletePersistedPath(skippedNode.FullPath, skippedNode.IsDirectory);
                RemoveFromIndex(skippedNode.FullPath);
            }

            return true;
        }

        if (_pathToNode.TryGetValue(path, out var existingNode))
        {
            if (existingNode.IsDirectory == isDirectory)
            {
                return existingNode.IsDirectory
                    ? _db.GetDirectoryByPath(path) is not null
                    : _db.GetFileByPath(path) is not null;
            }

            DeletePersistedPath(existingNode.FullPath, existingNode.IsDirectory);
            RemoveFromIndex(existingNode.FullPath);
        }

        var parentPath = Path.GetDirectoryName(path);
        if (parentPath == null || !_pathToNode.TryGetValue(parentPath, out var parentNode))
        {
            return false;
        }

        if (isDirectory)
        {
            return AddDirectoryTreeToIndex(path, parentNode, ct);
        }

        return AddFileToIndex(path, parentNode);
    }

    private bool HandleDeleted(FileChangeEvent evt)
    {
        var eventPath = NormalizeIndexedPath(evt.FullPath);
        _pathToNode.TryGetValue(eventPath, out var existingNode);
        var persistedPath = existingNode?.FullPath ?? eventPath;
        var isDirectory = ResolveIsDirectory(persistedPath, existingNode, evt.IsDirectory);

        DeletePersistedPath(persistedPath, isDirectory);
        RemoveFromIndex(persistedPath);
        return true;
    }

    private bool HandleRenamed(FileChangeEvent evt)
    {
        if (evt.OldPath == null)
        {
            return HandleCreated(evt);
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
            return true;
        }

        return AddPathToIndex(newPath, wasDirectory);
    }

    private bool HandleModified(FileChangeEvent evt)
    {
        var path = NormalizeIndexedPath(evt.FullPath);
        if (ShouldSkipReparsePath(path))
        {
            if (_pathToNode.TryGetValue(path, out var skippedNode))
            {
                DeletePersistedPath(skippedNode.FullPath, skippedNode.IsDirectory);
                RemoveFromIndex(skippedNode.FullPath);
            }

            return true;
        }

        if (!_pathToNode.TryGetValue(path, out var node))
        {
            return !File.Exists(path) && !Directory.Exists(path);
        }

        if (node.IsDirectory || node.Metadata is null)
        {
            return true;
        }

        try
        {
            var fi = new FileInfo(path);
            var existing = _db.GetFileByPath(path);
            if (existing is null)
            {
                return false;
            }

            existing.LastWriteTimeUtc = fi.LastWriteTimeUtc.Ticks;
            existing.SizeBytes = fi.Length;
            existing.LastIndexedTimeUtc = DateTime.UtcNow.Ticks;
            _db.InsertFile(existing);

            node.Metadata.SizeBytes = fi.Length;
            node.Metadata.LastWriteTime = fi.LastWriteTime;
        }
        catch (Exception ex)
        {
            NotifyError($"Error updating file metadata {path}: {ex.Message}");
            return false;
        }

        return true;
    }

    #endregion

    #region Index Helpers

    private void ResetInMemoryIndex()
    {
        if (UsesCompactCatalog)
        {
            ResetCompactInMemoryIndex();
            return;
        }
        _pathToNode.Clear();
        _detachedNodeCount = 0;
        _rootNode = null;
        Volatile.Write(ref _publishedSearchState, ISearchStateReader.Empty(_layout));
    }

    private void PublishSearchStateFromCurrentIndex()
    {
        if (UsesCompactCatalog) return;
        var startedAt = DateTime.Now;
        var timestamp = Stopwatch.GetTimestamp();

        Volatile.Write(
            ref _publishedSearchState,
            ISearchStateReader.Create(
                _layout,
                _pathToNode.Values.Where(node => !ReferenceEquals(node, _rootNode)),
                _tokenizer));

        Interlocked.Increment(ref _republishCount);
        Interlocked.Exchange(ref _lastRepublishAtTicks, startedAt.Ticks);
        Interlocked.Exchange(
            ref _lastRepublishDurationTicks,
            Stopwatch.GetElapsedTime(timestamp).Ticks);
    }

    private void PublishReconciliationChanges(HashSet<string> changedPaths)
    {
        var current = CurrentSearchState;
        changedPaths.RemoveWhere(path => !current.ContainsPath(path) && !_pathToNode.ContainsKey(path));
        var indexedCount = _pathToNode.Count;
        if (_rootNode != null && _pathToNode.TryGetValue(_rootNode.FullPath, out var root) && ReferenceEquals(root, _rootNode))
        {
            indexedCount--;
            changedPaths.Remove(_rootNode.FullPath);
        }
        var itemCount = Math.Max(current.ItemCount, indexedCount);
        if (itemCount == 0 || (long)changedPaths.Count * 10 >= itemCount)
        {
            PublishSearchStateFromCurrentIndex();
            return;
        }

        var startedAt = DateTime.Now;
        var timestamp = Stopwatch.GetTimestamp();
        var removed = new List<string>();
        var upserts = new List<FileSystemNode>();
        foreach (var path in changedPaths)
        {
            if (_pathToNode.TryGetValue(path, out var node) && !ReferenceEquals(node, _rootNode))
                upserts.Add(node);
            else
                removed.Add(path);
        }

        Volatile.Write(ref _publishedSearchState, current.WithChanges(removed, upserts, _tokenizer));
        Interlocked.Increment(ref _incrementalReconciliationPublishCount);
        Interlocked.Increment(ref _republishCount);
        Interlocked.Exchange(ref _lastRepublishAtTicks, startedAt.Ticks);
        Interlocked.Exchange(ref _lastRepublishDurationTicks, Stopwatch.GetElapsedTime(timestamp).Ticks);
    }

    private void PublishSearchStateForFileChange(FileChangeEvent evt)
    {
        var currentState = CurrentSearchState;
        var currentPath = NormalizeIndexedPath(evt.FullPath);

        if (evt.ChangeType is FileChangeType.Deleted or FileChangeType.Renamed)
        {
            var removedPath = evt.ChangeType == FileChangeType.Renamed && evt.OldPath != null
                ? NormalizeIndexedPath(evt.OldPath)
                : currentPath;
            currentState = currentState.WithoutPathAndDescendants(removedPath);
        }

        if (evt.ChangeType != FileChangeType.Deleted)
        {
            currentState = currentState.WithUpserts(
                CollectIndexedSubtree(currentPath)
                    .Where(node => !ReferenceEquals(node, _rootNode)),
                _tokenizer);
        }

        Volatile.Write(ref _publishedSearchState, currentState);
    }

    private bool AddDirectoryTreeToIndex(
        string directoryPath,
        FileSystemNode parentNode,
        CancellationToken ct = default)
    {
        if (!Directory.Exists(directoryPath) ||
            ShouldSkipReparsePath(directoryPath))
            return true;

        var parentDir = _db.GetDirectoryByPath(parentNode.FullPath);
        if (parentDir == null)
        {
            NotifyError($"Cannot index directory because its parent is missing from the database: {directoryPath}");
            return false;
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
            _reconciliationChangedPaths?.Add(directoryPath);

            var processedItems = 0;
            var complete = ScanDirectoryRecursive(
                directoryPath,
                node,
                directoryId,
                indexedDir.Depth + 1,
                ref processedItems,
                totalItems: 1,
                ct,
                reportProgress: false);

            if (!complete)
            {
                transaction.Rollback();
                RemoveFromIndex(directoryPath);
                return false;
            }

            transaction.Commit();
            return true;
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

    private bool AddFileToIndex(string filePath, FileSystemNode parentNode)
    {
        if (_pathToNode.ContainsKey(filePath) ||
            !File.Exists(filePath) ||
            ShouldSkipReparsePath(filePath))
            return true;

        try
        {
            var fi = new FileInfo(filePath);
            var parentDir = _db.GetDirectoryByPath(parentNode.FullPath);
            if (parentDir == null)
            {
                NotifyError($"Cannot index file because its parent is missing from the database: {filePath}");
                return false;
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
            _reconciliationChangedPaths?.Add(filePath);
            return true;
        }
        catch (Exception ex)
        {
            NotifyError($"Error indexing file {filePath}: {ex.Message}");
            return false;
        }
    }

    private void RemoveFromIndex(string path)
    {
        path = NormalizeIndexedPath(path);
        if (!_pathToNode.TryGetValue(path, out var rootNode)) return;

        var normalizedRoot = NormalizeIndexedPath(rootNode.FullPath);

        var nodesToRemove = CollectIndexedSubtree(normalizedRoot)
            .OrderByDescending(node => node.FullPath.Length)
            .ToList();

        foreach (var node in nodesToRemove)
        {
            node.Parent?.RemoveChild(node.FullPath);
            _pathToNode.Remove(node.FullPath);
            _reconciliationChangedPaths?.Add(node.FullPath);
        }
    }

    private bool ShouldSkipReparsePath(string path)
    {
        return _skipReparsePoints &&
               _measurementPathGuard != null &&
               _measurementPathGuard.FindReparsePointInExistingPath(path) != null;
    }

    private void EnsureMeasurementDirectorySafe(string path)
    {
        if (!_enforceMeasurementPathSafety)
        {
            return;
        }

        if (!Directory.Exists(path) || ShouldSkipReparsePath(path))
        {
            throw new InvalidOperationException(
                "Ölçüm corpus'u eksik veya yeniden yönlendirilmiş bir yoldan okunamaz.");
        }
    }

    #endregion

    #region Progress Reporting

    private void ReportProgress(
        string status,
        int percentage,
        int itemCount,
        long elapsedMs,
        bool isIndeterminate = false)
    {
        var progress = new IndexProgress
        {
            Status = status,
            Percentage = percentage,
            ItemCount = itemCount,
            ElapsedMs = elapsedMs,
            IsIndeterminate = isIndeterminate
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
            if (UsesCompactCatalog) return CreateCompactNodeProjection().GetNode(normalizedPath);
            return _pathToNode.TryGetValue(normalizedPath, out var node) ? node : null;
        }
    }

    public ISearchStateReader CreateSearchState(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return CurrentSearchState;
    }

    public void IncrementOpenCount(string path)
    {
        if (UsesCompactCatalog)
        {
            IncrementCompactOpenCount(path);
            return;
        }
        lock (_lock)
        {
            var normalizedPath = NormalizeIndexedPath(path);
            _db.IncrementOpenCount(normalizedPath);

            if (_pathToNode.TryGetValue(normalizedPath, out var node) &&
                !node.IsDirectory && node.Metadata is { } metadata)
            {
                metadata.OpenCount++;
                Volatile.Write(
                    ref _publishedSearchState,
                    CurrentSearchState.WithUpserts(new[] { node }, _tokenizer));
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
        lock (_lock)
        {
            return new IndexStats
            {
                FileCount = _db.GetFileCount(),
                DirectoryCount = _db.GetDirectoryCount(),
                TokenCount = CurrentSearchState.TokenCount,
                DatabasePath = _db.DatabasePath,
                LastScanTime = GetLastScanTime()
            };
        }
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
                            lock (_lock)
                            {
                                _db.Dispose();
                            }
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

    internal static bool IsCanonicalIndexedPath(string path)
    {
        return string.Equals(path, NormalizeIndexedPath(path), StringComparison.Ordinal);
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

    private void NotifyDeltaSyncStateChanged(bool isRunning)
    {
        QueueNotification(() => OnDeltaSyncStateChanged?.Invoke(isRunning));
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
    public bool IsIndeterminate { get; set; }
}

public class IndexStats
{
    public int FileCount { get; set; }
    public int DirectoryCount { get; set; }
    public int TokenCount { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public DateTime? LastScanTime { get; set; }
}
