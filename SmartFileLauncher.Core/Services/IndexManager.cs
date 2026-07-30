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


// İki indexin modunu yönetir:
// - İlk çalıştırma: Tamamen diski tarar Sqlite kaydeder
// - Sonraki Çalıştırma: Load from SQLite + delta sync + FileSystemWatcher

public class IndexManager : IDisposable
{
    private readonly IndexDatabase _db;
    private readonly FileWatcherService _watcher;
    private readonly ITokenizer _tokenizer;
    private readonly object _lock = new();
    private readonly object _notificationLock = new();
    private readonly SemaphoreSlim _lifecycleGate = new(1, 1);
    private Task _notificationTask = Task.CompletedTask;
    private CancellationTokenSource? _backgroundSyncCts;
    private Task? _backgroundSyncTask;
    
    // In-memory structures (loaded from DB or built fresh)
    private InvertedIndex _invertedIndex;
    private Dictionary<string, FileMetadata> _metadataMap;
    private FileSystemNode? _rootNode;
    private Dictionary<string, FileSystemNode> _pathToNode;
    
    private volatile bool _disposed;
    private bool _isInitialized;
    
    // Background delta sync tracking
    private bool _isDeltaSyncRunning = false;
    private int _deltaSyncProgress = 0;
    private int _deltaSyncTotal = 0;
    private int _deltaSyncProcessed = 0;
    private readonly HashSet<string> _syncedPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _currentlySyncing = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Progress reporting during indexing.
    /// </summary>
    public event Action<IndexProgress>? OnProgress;

    /// <summary>
    /// Fired when a file system change is processed.
    /// </summary>
    public event Action<FileChangeEvent>? OnFileChange;

    /// <summary>
    /// Fired when an error occurs.
    /// </summary>
    public event Action<string>? OnError;
    
    /// <summary>
    /// Fired when background delta sync progress changes.
    /// Args: (processed, total, percentage)
    /// </summary>
    public event Action<int, int, int>? OnDeltaSyncProgress;

    public IndexManager(ITokenizer? tokenizer = null)
        : this(new IndexDatabase(), new FileWatcherService(), tokenizer)
    {
    }

    internal IndexManager(IndexDatabase database, FileWatcherService watcher, ITokenizer? tokenizer = null)
    {
        _tokenizer = tokenizer ?? new BasicTokenizer();
        _db = database;
        _watcher = watcher;
        _invertedIndex = new InvertedIndex();
        _metadataMap = new Dictionary<string, FileMetadata>();
        _pathToNode = new Dictionary<string, FileSystemNode>(StringComparer.OrdinalIgnoreCase);

        _watcher.OnChange += HandleFileChange;
        _watcher.OnError += ex => NotifyError(ex.Message);
    }

    #region Properties

    public InvertedIndex InvertedIndex => _invertedIndex;
    public Dictionary<string, FileMetadata> MetadataMap => _metadataMap;
    public FileSystemNode? RootNode => _rootNode;
    public bool IsInitialized => _isInitialized;
    public string DatabasePath => _db.DatabasePath;

    public int IndexedFileCount => _invertedIndex.NodeCount;
    public int IndexedTokenCount => _invertedIndex.TokenCount;
    
    // Background delta sync properties
    public bool IsDeltaSyncRunning => _isDeltaSyncRunning;
    public bool IsDeltaSyncComplete => !_isDeltaSyncRunning;
    public int DeltaSyncProgress => _deltaSyncProgress;
    public int DeltaSyncProcessed => _deltaSyncProcessed;
    public int DeltaSyncTotal => _deltaSyncTotal;

    #endregion

    #region Initialization

    /// <summary>
    /// Initialize the index manager with multiple root directories.
    /// Loads from cache if available, otherwise performs full scan.
    /// </summary>
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

        // Check if we have a cached index for these roots
        var cachedRoot = _db.GetMetadata(IndexMetadata.Keys.ScanRootPath);
        var newRootsKey = string.Join("|", paths.OrderBy(p => p, StringComparer.OrdinalIgnoreCase));
        var hasCache = cachedRoot != null && 
                       cachedRoot.Equals(newRootsKey, StringComparison.OrdinalIgnoreCase) &&
                       _db.GetFileCount() > 0;

        if (hasCache)
        {
            ReportProgress("Önbellekten yükleniyor...", 0, 0, 0);
            await LoadFromCacheMultiAsync(paths, ct);
            
            // Start background delta sync (non-blocking).
            var syncCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            _backgroundSyncCts = syncCts;
            _backgroundSyncTask = Task.Run(
                () => BackgroundDeltaSyncAsync(paths, syncCts.Token),
                syncCts.Token);
        }
        else
        {
            // Fresh bootstrap scan
            ReportProgress("İlk kurulum - dosyalar taranıyor...", 0, 0, 0);
            await BootstrapScanMultiAsync(paths, ct);
        }

        // Configure every root first, then start the shared event processor once.
        SetupWatchers(paths);

        sw.Stop();
        _db.SetMetadata(IndexMetadata.Keys.LastBuildDurationMs, sw.ElapsedMilliseconds.ToString());
        _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, newRootsKey);

        _isInitialized = true;
        ReportProgress("Hazır", 100, _invertedIndex.NodeCount, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Initialize the index manager with a single root directory.
    /// Loads from cache if available, otherwise performs full scan.
    /// </summary>
    public async Task InitializeAsync(string rootPath, CancellationToken ct = default)
    {
        await InitializeAsync(new[] { rootPath }, ct);
    }

    /// <summary>
    /// Force a complete rescan (clears existing index).
    /// </summary>
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
            SetupWatchers(new[] { normalizedRootPath });
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

        // Clear existing data
        _db.ClearIndex();
        lock (_lock)
        {
            ResetInMemoryIndex();
        }

        // Create root node
        var rootName = Path.GetFileName(rootPath);
        if (string.IsNullOrEmpty(rootName)) rootName = rootPath;
        _rootNode = new FileSystemNode(rootName, rootPath, true);
        _pathToNode[rootPath] = _rootNode;

        // Count total items for progress
        int totalItems = 0;
        int processedItems = 0;

        await Task.Run(() =>
        {
            // First pass: count items
            try
            {
                totalItems = CountItems(rootPath);
            }
            catch
            {
                totalItems = 100; // fallback estimate
            }

            // Begin transaction for bulk insert
            using var transaction = _db.BeginTransaction();

            try
            {
                // Insert root directory
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

                // Scan recursively
                ScanDirectoryRecursive(rootPath, _rootNode, rootDirId, 1, ref processedItems, totalItems, ct);

                transaction.Commit();
            }
            catch
            {
                transaction.Rollback();
                throw;
            }
        }, ct);

        // Save metadata
        _db.SetMetadata(IndexMetadata.Keys.ScanRootPath, rootPath);
        _db.SetMetadata(IndexMetadata.Keys.LastFullScanTime, DateTime.UtcNow.Ticks.ToString());
        _db.SetMetadata(IndexMetadata.Keys.TotalFilesIndexed, _invertedIndex.NodeCount.ToString());

        sw.Stop();
        ReportProgress("Tarama tamamlandı", 100, _invertedIndex.NodeCount, sw.ElapsedMilliseconds);
    }

    /// <summary>
    /// Bootstrap scan for multiple root directories.
    /// </summary>
    private async Task BootstrapScanMultiAsync(List<string> rootPaths, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        // Clear existing data
        _db.ClearIndex();
        lock (_lock)
        {
            ResetInMemoryIndex();
        }

        // Create virtual root node that contains all paths
        _rootNode = new FileSystemNode("Root", "", true);

        // Count total items for progress
        int totalItems = 0;
        int processedItems = 0;

        await Task.Run(() =>
        {
            // First pass: count items in all paths
            foreach (var rootPath in rootPaths)
            {
                if (!Directory.Exists(rootPath)) continue;
                try
                {
                    totalItems += CountItems(rootPath);
                }
                catch
                {
                    totalItems += 100; // fallback estimate
                }
            }

            // Begin transaction for bulk insert
            using var transaction = _db.BeginTransaction();

            try
            {
                foreach (var rootPath in rootPaths)
                {
                    if (!Directory.Exists(rootPath)) continue;
                    
                    var rootName = Path.GetFileName(rootPath);
                    if (string.IsNullOrEmpty(rootName)) rootName = rootPath;
                    
                    // Create node for this root
                    var rootPathNode = new FileSystemNode(rootName, rootPath, true);
                    _rootNode.AddChild(rootPathNode);
                    _pathToNode[rootPath] = rootPathNode;

                    // Insert root directory
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

                    // Scan recursively
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

        // Save metadata
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
            // Process subdirectories
            foreach (var dir in Directory.GetDirectories(path))
            {
                ct.ThrowIfCancellationRequested();

                var dirName = Path.GetFileName(dir);
                var dirInfo = new DirectoryInfo(dir);

                // Skip hidden/system directories
                if ((dirInfo.Attributes & FileAttributes.Hidden) != 0 ||
                    (dirInfo.Attributes & FileAttributes.System) != 0)
                    continue;

                var dirNode = new FileSystemNode(dirName, dir, true);
                parentNode.AddChild(dirNode);
                _pathToNode[dir] = dirNode;

                // Index directory name tokens
                IndexNode(dirNode);

                // Insert into DB
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

                // Recurse
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

            // Process files
            foreach (var file in Directory.GetFiles(path))
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var fi = new FileInfo(file);

                    // Skip hidden/system files
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

                    // Index file name tokens
                    IndexNode(fileNode);

                    // Add to metadata map
                    _metadataMap[file] = fileNode.Metadata!;

                    // Insert into DB
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
            // Create root node
            var rootName = Path.GetFileName(rootPath);
            if (string.IsNullOrEmpty(rootName)) rootName = rootPath;
            _rootNode = new FileSystemNode(rootName, rootPath, true);
            _pathToNode[rootPath] = _rootNode;

            // Load directories first (to build tree structure)
            var dirMap = new Dictionary<long, (IndexedDirectory Dir, FileSystemNode Node)>();
            long? rootDirId = null;

            foreach (var dir in _db.GetAllDirectories())
            {
                ct.ThrowIfCancellationRequested();

                // Root dizini özel olarak işle
                if (dir.FullPath.Equals(rootPath, StringComparison.OrdinalIgnoreCase))
                {
                    rootDirId = dir.Id;
                    dirMap[dir.Id] = (dir, _rootNode);
                    // Root için token indexleme
                    IndexNode(_rootNode);
                    continue;
                }

                var node = new FileSystemNode(dir.Name, dir.FullPath, true);
                _pathToNode[dir.FullPath] = node;
                dirMap[dir.Id] = (dir, node);

                // Index tokens
                IndexNode(node);
            }

            // Build tree relationships
            foreach (var (id, (dir, node)) in dirMap)
            {
                // Root'u atla
                if (node == _rootNode) continue;
                
                if (dir.ParentId.HasValue && dirMap.TryGetValue(dir.ParentId.Value, out var parent))
                {
                    parent.Node.AddChild(node);
                }
                else
                {
                    // Parent bulunamadı, root'a ekle
                    _rootNode.AddChild(node);
                }
            }

            // Load files
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

                // Try to find parent by DirectoryId first, then by path
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

    /// <summary>
    /// Load from cache for multiple root directories.
    /// </summary>
    private async Task LoadFromCacheMultiAsync(List<string> rootPaths, CancellationToken ct)
    {
        var sw = Stopwatch.StartNew();

        lock (_lock)
        {
            ResetInMemoryIndex();
        }

        await Task.Run(() =>
        {
            // Create virtual root node
            _rootNode = new FileSystemNode("Root", "", true);

            // Create nodes for each root path
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

            // Load directories (to build tree structure)
            var dirMap = new Dictionary<long, (IndexedDirectory Dir, FileSystemNode Node)>();

            foreach (var dir in _db.GetAllDirectories())
            {
                ct.ThrowIfCancellationRequested();

                // Check if this is one of our root paths
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

            // Build tree relationships
            foreach (var (id, (dir, node)) in dirMap)
            {
                if (rootPathNodes.ContainsKey(dir.FullPath)) continue;
                
                if (dir.ParentId.HasValue && dirMap.TryGetValue(dir.ParentId.Value, out var parent))
                {
                    parent.Node.AddChild(node);
                }
                else
                {
                    // Find which root path this belongs to
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

            // Load files
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

                // Try to find parent by DirectoryId first, then by path
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
                        // Check if file is directly in one of the root paths
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

    #region Delta Sync

    private async Task DeltaSyncAsync(string rootPath, CancellationToken ct)
    {
        var changes = 0;

        await Task.Run(() =>
        {
            // Check each cached file still exists and hasn't changed
            var filesToRemove = new List<string>();
            var filesToUpdate = new List<string>();

            foreach (var file in _db.GetAllFiles())
            {
                ct.ThrowIfCancellationRequested();

                if (!File.Exists(file.FullPath))
                {
                    filesToRemove.Add(file.FullPath);
                }
                else
                {
                    var fi = new FileInfo(file.FullPath);
                    if (fi.LastWriteTimeUtc.Ticks != file.LastWriteTimeUtc)
                    {
                        filesToUpdate.Add(file.FullPath);
                    }
                }
            }

            // Process removals
            foreach (var path in filesToRemove)
            {
                _db.DeleteFile(path);
                RemoveFromIndex(path);
                changes++;
            }

            // Process updates (re-index)
            foreach (var path in filesToUpdate)
            {
                if (_pathToNode.TryGetValue(path, out var node))
                {
                    // Update metadata
                    var fi = new FileInfo(path);
                    if (node.Metadata != null)
                    {
                        node.Metadata.SizeBytes = fi.Length;
                        node.Metadata.LastWriteTime = fi.LastWriteTime;
                    }

                    // Update DB
                    var existing = _db.GetFileByPath(path);
                    if (existing != null)
                    {
                        existing.LastWriteTimeUtc = fi.LastWriteTimeUtc.Ticks;
                        existing.SizeBytes = fi.Length;
                        existing.LastIndexedTimeUtc = DateTime.UtcNow.Ticks;
                        _db.InsertFile(existing); // upsert
                    }
                }
                changes++;
            }

            // Check for new files in all indexed directories
            // Scan all directories that are in _pathToNode for new files
            try
            {
                var indexedDirs = _pathToNode
                    .Where(kvp => kvp.Value.IsDirectory)
                    .Select(kvp => kvp.Key)
                    .ToList();

                foreach (var dirPath in indexedDirs)
                {
                    ct.ThrowIfCancellationRequested();
                    
                    if (!Directory.Exists(dirPath)) continue;
                    if (!_pathToNode.TryGetValue(dirPath, out var parentNode)) continue;

                    try
                    {
                        foreach (var file in Directory.GetFiles(dirPath))
                        {
                            if (!_pathToNode.ContainsKey(file))
                            {
                                AddFileToIndex(file, parentNode);
                                changes++;
                            }
                        }
                    }
                    catch { /* Access denied or other IO error */ }
                }
            }
            catch { }

        }, ct);

        if (changes > 0)
        {
            ReportProgress($"Delta senk: {changes} değişiklik.", 100, _invertedIndex.NodeCount, 0);
        }
    }
    
    /// <summary>
    /// Background delta sync - runs in background without blocking UI.
    /// Reports progress via OnDeltaSyncProgress event.
    /// </summary>
    private async Task BackgroundDeltaSyncAsync(List<string> rootPaths, CancellationToken ct)
    {
        _isDeltaSyncRunning = true;
        _deltaSyncProgress = 0;
        _deltaSyncProcessed = 0;
        
        try
        {
            await Task.Run(() =>
            {
                var allFiles = _db.GetAllFiles().ToList();
                _deltaSyncTotal = allFiles.Count;
                
                var filesToRemove = new List<string>();
                var filesToUpdate = new List<string>();
                
                // Check each cached file
                for (int i = 0; i < allFiles.Count; i++)
                {
                    if (ct.IsCancellationRequested) break;
                    
                    var file = allFiles[i];
                    
                    if (!File.Exists(file.FullPath))
                    {
                        filesToRemove.Add(file.FullPath);
                    }
                    else
                    {
                        var fi = new FileInfo(file.FullPath);
                        if (fi.LastWriteTimeUtc.Ticks != file.LastWriteTimeUtc)
                        {
                            filesToUpdate.Add(file.FullPath);
                        }
                    }
                    
                    _deltaSyncProcessed = i + 1;
                    _deltaSyncProgress = (_deltaSyncProcessed * 100) / _deltaSyncTotal;
                    
                    // Report progress every 100 files
                    if (_deltaSyncProcessed % 100 == 0 || _deltaSyncProcessed == _deltaSyncTotal)
                    {
                        NotifyDeltaSyncProgress(_deltaSyncProcessed, _deltaSyncTotal, _deltaSyncProgress);
                    }
                    
                    // Yield to other threads occasionally
                    if (i % 50 == 0)
                    {
                        Thread.Sleep(1);
                    }
                }
                
                // Process removals
                lock (_lock)
                {
                    foreach (var path in filesToRemove)
                    {
                        _db.DeleteFile(path);
                        RemoveFromIndex(path);
                    }
                }
                
                // Process updates
                lock (_lock)
                {
                    foreach (var path in filesToUpdate)
                    {
                        if (_pathToNode.TryGetValue(path, out var node))
                        {
                            var fi = new FileInfo(path);
                            if (node.Metadata != null)
                            {
                                node.Metadata.SizeBytes = fi.Length;
                                node.Metadata.LastWriteTime = fi.LastWriteTime;
                            }
                            
                            var existing = _db.GetFileByPath(path);
                            if (existing != null)
                            {
                                existing.LastWriteTimeUtc = fi.LastWriteTimeUtc.Ticks;
                                existing.SizeBytes = fi.Length;
                                existing.LastIndexedTimeUtc = DateTime.UtcNow.Ticks;
                                _db.InsertFile(existing);
                            }
                        }
                    }
                }
                
                // Check for new files in all indexed directories
                var indexedDirs = _pathToNode
                    .Where(kvp => kvp.Value.IsDirectory)
                    .Select(kvp => kvp.Key)
                    .ToList();
                
                foreach (var dirPath in indexedDirs)
                {
                    if (ct.IsCancellationRequested) break;
                    
                    if (!Directory.Exists(dirPath)) continue;
                    if (!_pathToNode.TryGetValue(dirPath, out var parentNode)) continue;
                    
                    try
                    {
                        foreach (var file in Directory.GetFiles(dirPath))
                        {
                            if (!_pathToNode.ContainsKey(file))
                            {
                                lock (_lock)
                                {
                                    AddFileToIndex(file, parentNode);
                                }
                            }
                        }
                    }
                    catch { /* Access denied or other IO error */ }
                }
                
            }, ct);
        }
        catch (OperationCanceledException)
        {
            // Cancelled - that's fine
        }
        catch (Exception ex)
        {
            NotifyError($"Background delta sync error: {ex.Message}");
        }
        finally
        {
            _isDeltaSyncRunning = false;
            _deltaSyncProgress = 100;
            NotifyDeltaSyncProgress(_deltaSyncTotal, _deltaSyncTotal, 100);
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
            // Cancellation is the expected lifecycle transition.
        }
        catch (Exception ex)
        {
            NotifyError($"Background delta sync shutdown error: {ex.Message}");
        }
        finally
        {
            syncCts?.Dispose();
        }
    }

    /// <summary>
    /// Ensures a specific path is synced. If delta sync hasn't processed it yet,
    /// performs a quick on-demand sync for just that path.
    /// OPTIMIZED: Non-blocking wait with timeout to prevent deadlock.
    /// </summary>
    public async Task<bool> EnsureSyncedAsync(string path, CancellationToken ct = default)
    {
        // If delta sync is complete, we're already synced
        if (IsDeltaSyncComplete)
        {
            _syncedPaths.Add(path);
            return true;
        }
        
        // Check if already synced
        lock (_lock)
        {
            if (_syncedPaths.Contains(path))
                return true;
        }
        
        // Check if currently syncing (with timeout to prevent infinite wait)
        var maxWaitTime = TimeSpan.FromSeconds(30);
        var startTime = DateTime.UtcNow;
        
        while (true)
        {
            bool isCurrentlySyncing;
            lock (_lock)
            {
                isCurrentlySyncing = _currentlySyncing.Contains(path);
                if (!isCurrentlySyncing)
                {
                    _currentlySyncing.Add(path);
                    break;
                }
            }
            
            if (isCurrentlySyncing)
            {
                // Another task is syncing this path - wait a bit
                if (DateTime.UtcNow - startTime > maxWaitTime)
                {
                    NotifyError($"Timeout waiting for sync of {path}");
                    return false;
                }
                
                await Task.Delay(50, ct);
            }
        }
        
        try
        {
            await QuickSyncPathAsync(path, ct);
            
            lock (_lock)
            {
                _syncedPaths.Add(path);
            }
            
            return true;
        }
        catch (OperationCanceledException)
        {
            return false;
        }
        catch (Exception ex)
        {
            NotifyError($"On-demand sync error for {path}: {ex.Message}");
            return false;
        }
        finally
        {
            lock (_lock)
            {
                _currentlySyncing.Remove(path);
            }
        }
    }
    
    /// <summary>
    /// Quick sync for a specific directory path.
    /// Checks only files in this directory (non-recursive).
    /// OPTIMIZED for large folders (10,000+ files).
    /// </summary>
    private async Task QuickSyncPathAsync(string dirPath, CancellationToken ct)
    {
        await Task.Run(() =>
        {
            if (!Directory.Exists(dirPath)) return;
            
            // OPTIMIZATION: Use enumerable instead of loading all files into memory
            var diskFilesSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            
            try
            {
                foreach (var file in Directory.EnumerateFiles(dirPath))
                {
                    ct.ThrowIfCancellationRequested();
                    diskFilesSet.Add(file);
                }
            }
            catch (Exception ex)
            {
                NotifyError($"Error enumerating files in {dirPath}: {ex.Message}");
                return;
            }
            
            // Get files from cache
            HashSet<string> cachedFiles;
            lock (_lock)
            {
                cachedFiles = _pathToNode
                    .Where(kvp => !kvp.Value.IsDirectory && 
                                  string.Equals(Path.GetDirectoryName(kvp.Key), dirPath, StringComparison.OrdinalIgnoreCase))
                    .Select(kvp => kvp.Key)
                    .ToHashSet(StringComparer.OrdinalIgnoreCase);
            }
            
            // Find differences
            var newFiles = diskFilesSet.Except(cachedFiles).ToList();
            var deletedFiles = cachedFiles.Except(diskFilesSet).ToList();
            
            // OPTIMIZATION: Batch operations with transaction
            if (newFiles.Any() || deletedFiles.Any())
            {
                using var transaction = _db.BeginTransaction();
                
                try
                {
                    // Add new files in batch
                    if (newFiles.Any())
                    {
                        FileSystemNode? parentNode = null;
                        lock (_lock)
                        {
                            _pathToNode.TryGetValue(dirPath, out parentNode);
                        }
                        
                        if (parentNode != null)
                        {
                            // Process in smaller chunks to allow cancellation
                            for (int i = 0; i < newFiles.Count; i++)
                            {
                                if (i % 100 == 0)
                                {
                                    ct.ThrowIfCancellationRequested();
                                    Thread.Sleep(1); // Yield to other threads
                                }
                                
                                lock (_lock)
                                {
                                    AddFileToIndex(newFiles[i], parentNode);
                                }
                            }
                        }
                    }
                    
                    // Remove deleted files in batch
                    if (deletedFiles.Any())
                    {
                        for (int i = 0; i < deletedFiles.Count; i++)
                        {
                            if (i % 100 == 0)
                            {
                                ct.ThrowIfCancellationRequested();
                                Thread.Sleep(1);
                            }
                            
                            lock (_lock)
                            {
                                _db.DeleteFile(deletedFiles[i]);
                                RemoveFromIndex(deletedFiles[i]);
                            }
                        }
                    }
                    
                    transaction.Commit();
                }
                catch
                {
                    transaction.Rollback();
                    throw;
                }
            }
            
            // Check for modified files (sample only if too many)
            var filesToCheck = cachedFiles.Intersect(diskFilesSet).ToList();
            int checkCount = Math.Min(filesToCheck.Count, 1000); // Limit to 1000 for performance
            
            for (int i = 0; i < checkCount; i++)
            {
                if (i % 50 == 0)
                {
                    ct.ThrowIfCancellationRequested();
                }
                
                var file = filesToCheck[i];
                
                try
                {
                    var fi = new FileInfo(file);
                    var cached = _db.GetFileByPath(file);
                    
                    if (cached != null && fi.LastWriteTimeUtc.Ticks != cached.LastWriteTimeUtc)
                    {
                        lock (_lock)
                        {
                            if (_pathToNode.TryGetValue(file, out var node) && node.Metadata != null)
                            {
                                node.Metadata.SizeBytes = fi.Length;
                                node.Metadata.LastWriteTime = fi.LastWriteTime;
                            }
                            
                            cached.LastWriteTimeUtc = fi.LastWriteTimeUtc.Ticks;
                            cached.SizeBytes = fi.Length;
                            cached.LastIndexedTimeUtc = DateTime.UtcNow.Ticks;
                            _db.InsertFile(cached);
                        }
                    }
                }
                catch { /* Skip files with access errors */ }
            }
            
        }, ct);
    }

    #endregion

    #region FileWatcher Integration

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

        // User callbacks are external code. Invoke them after releasing the index
        // lock so lifecycle operations (including Dispose) cannot deadlock.
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

    private void AddPathToIndex(string path, bool isDirectory)
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
            AddDirectoryTreeToIndex(path, parentNode);
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

        // A rename can race with a subsequent delete. Do not add a phantom path.
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

                // Update DB
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
        _syncedPaths.Clear();
        _currentlySyncing.Clear();
        _rootNode = null;
    }

    private void AddDirectoryTreeToIndex(string directoryPath, FileSystemNode parentNode)
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
                CancellationToken.None,
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
            node.Parent?.Children.RemoveAll(child =>
                string.Equals(child.FullPath, node.FullPath, StringComparison.OrdinalIgnoreCase));
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

    /// <summary>
    /// Get a node by its path.
    /// </summary>
    public FileSystemNode? GetNode(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        var normalizedPath = NormalizeIndexedPath(path);
        return _pathToNode.TryGetValue(normalizedPath, out var node) ? node : null;
    }

    /// <summary>
    /// Increment the open count for a file (for frequency-based scoring).
    /// </summary>
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

    /// <summary>
    /// Get index statistics.
    /// </summary>
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
                    try
                    {
                        _watcher.Dispose();
                    }
                    finally
                    {
                        _db.Dispose();
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

/// <summary>
/// Progress information during indexing.
/// </summary>
public class IndexProgress
{
    public string Status { get; set; } = string.Empty;
    public int Percentage { get; set; }
    public int ItemCount { get; set; }
    public long ElapsedMs { get; set; }
}

/// <summary>
/// Index statistics.
/// </summary>
public class IndexStats
{
    public int FileCount { get; set; }
    public int DirectoryCount { get; set; }
    public int TokenCount { get; set; }
    public string DatabasePath { get; set; } = string.Empty;
    public DateTime? LastScanTime { get; set; }
}
