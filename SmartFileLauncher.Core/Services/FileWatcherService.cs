using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Services;

public class FileWatcherService : IDisposable
{
    [ThreadStatic]
    private static FileWatcherService? _dispatchingService;

    private readonly object _stateLock = new();
    private readonly ConcurrentQueue<(FileChangeEvent Event, long Generation)> _eventQueue;
    private readonly HashSet<string> _excludedPaths;
    private readonly HashSet<string> _excludedExtensions;
    private readonly List<FileSystemWatcher> _watchers;
    private readonly CancellationTokenSource _cts;
    private readonly int _debounceMs;
    private Task? _processorTask;
    private long _generation;
    private int _inFlightCallbacks;
    private bool _isClearing;
    private bool _disposed;
    private volatile bool _isWatching;

    public event Action<FileChangeEvent>? OnChange;

    public event Action<Exception>? OnError;

    public FileWatcherService(int debounceMs = 100)
    {
        _eventQueue = new ConcurrentQueue<(FileChangeEvent Event, long Generation)>();
        _excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _excludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _watchers = new List<FileSystemWatcher>();
        _cts = new CancellationTokenSource();
        _debounceMs = debounceMs;

        AddExcludedPath(@"\$Recycle.Bin\");
        AddExcludedPath(@"\System Volume Information\");
        AddExcludedPath(@"\.git\");
        AddExcludedPath(@"\node_modules\");
        AddExcludedPath(@"\bin\");
        AddExcludedPath(@"\obj\");
        AddExcludedPath(@"\__pycache__\");
        
        AddExcludedExtension(".tmp");
        AddExcludedExtension(".temp");
        AddExcludedExtension(".partial");
        AddExcludedExtension(".crdownload");
    }

    public int PendingEventCount => _eventQueue.Count;

    public bool IsWatching => _isWatching;

    internal int WatchedPathCount
    {
        get
        {
            lock (_stateLock)
            {
                return _watchers.Count;
            }
        }
    }

    internal Task? ProcessorTask
    {
        get
        {
            lock (_stateLock)
            {
                return _processorTask;
            }
        }
    }

    #region Configuration

    public void AddExcludedPath(string pathPattern)
    {
        _excludedPaths.Add(pathPattern);
    }

    public void AddExcludedExtension(string extension)
    {
        if (!extension.StartsWith('.'))
            extension = "." + extension;
        _excludedExtensions.Add(extension);
    }

    public void RemoveExcludedPath(string pathPattern)
    {
        _excludedPaths.Remove(pathPattern);
    }

    #endregion

    #region Watch Control

    public void Watch(string path)
    {
        var fullPath = NormalizeDirectoryPath(path);
        if (!Directory.Exists(fullPath))
            throw new DirectoryNotFoundException($"Directory not found: {fullPath}");

        lock (_stateLock)
        {
            if (_isClearing && ReferenceEquals(_dispatchingService, this))
                return;

            while (_isClearing)
            {
                Monitor.Wait(_stateLock);
            }

            ThrowIfDisposed();

            if (_watchers.Any(w => string.Equals(w.Path, fullPath, StringComparison.OrdinalIgnoreCase)))
                return;

            var watcher = new FileSystemWatcher(fullPath)
            {
                NotifyFilter = NotifyFilters.FileName
                             | NotifyFilters.DirectoryName
                             | NotifyFilters.LastWrite
                             | NotifyFilters.Size,
                IncludeSubdirectories = true,
                EnableRaisingEvents = false
            };

            watcher.Created += OnFileCreated;
            watcher.Deleted += OnFileDeleted;
            watcher.Renamed += OnFileRenamed;
            watcher.Changed += OnFileChanged;
            watcher.Error += OnWatcherError;

            _watchers.Add(watcher);
            try
            {
                if (_isWatching)
                {
                    watcher.EnableRaisingEvents = true;
                }
            }
            catch
            {
                _watchers.Remove(watcher);
                watcher.Dispose();
                throw;
            }
        }
    }

    public void Start()
    {
        lock (_stateLock)
        {
            ThrowIfDisposed();

            if (_isWatching || _isClearing) return;

            if (_processorTask == null || _processorTask.IsCompleted)
            {
                _processorTask = Task.Run(() => ProcessEventsAsync(_cts.Token));
            }

            Interlocked.Increment(ref _generation);
            _isWatching = true;

            try
            {
                foreach (var watcher in _watchers)
                {
                    watcher.EnableRaisingEvents = true;
                }
            }
            catch
            {
                _isWatching = false;
                Interlocked.Increment(ref _generation);

                foreach (var watcher in _watchers)
                {
                    watcher.EnableRaisingEvents = false;
                }

                throw;
            }
        }
    }

    public void Stop()
    {
        lock (_stateLock)
        {
            if (_disposed || !_isWatching) return;

            foreach (var watcher in _watchers)
            {
                watcher.EnableRaisingEvents = false;
            }

            _isWatching = false;
            Interlocked.Increment(ref _generation);

            if (!ReferenceEquals(_dispatchingService, this))
            {
                while (_inFlightCallbacks > 0)
                {
                    Monitor.Wait(_stateLock);
                }
            }
        }
    }

    public void ClearWatches()
    {
        lock (_stateLock)
        {
            if (_isClearing && ReferenceEquals(_dispatchingService, this))
                return;

            while (_isClearing)
            {
                Monitor.Wait(_stateLock);
            }

            ThrowIfDisposed();
            _isClearing = true;

            try
            {
                _isWatching = false;
                Interlocked.Increment(ref _generation);

                foreach (var watcher in _watchers)
                {
                    watcher.EnableRaisingEvents = false;
                }

                if (!ReferenceEquals(_dispatchingService, this))
                {
                    while (_inFlightCallbacks > 0)
                    {
                        Monitor.Wait(_stateLock);
                    }
                }

                foreach (var watcher in _watchers)
                {
                    watcher.Dispose();
                }

                _watchers.Clear();
                while (_eventQueue.TryDequeue(out _)) { }
            }
            finally
            {
                _isWatching = false;
                _isClearing = false;
                Monitor.PulseAll(_stateLock);
            }
        }
    }

    #endregion

    #region Event Handlers

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        EnqueueEvent(FileChangeType.Created, e.FullPath, null, IsDirectory(e.FullPath));
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        bool isDir = string.IsNullOrEmpty(Path.GetExtension(e.FullPath));
        EnqueueEvent(FileChangeType.Deleted, e.FullPath, null, isDir);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        EnqueueEvent(FileChangeType.Renamed, e.FullPath, e.OldFullPath, IsDirectory(e.FullPath));
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        if (!IsDirectory(e.FullPath))
        {
            EnqueueEvent(FileChangeType.Modified, e.FullPath, null, false);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        NotifyError(e.GetException());
    }

    #endregion

    #region Event Processing

    private void EnqueueEvent(FileChangeType type, string path, string? oldPath, bool isDirectory)
    {
        if (type == FileChangeType.Renamed && oldPath != null)
        {
            var oldExcluded = ShouldExclude(oldPath);
            var newExcluded = ShouldExclude(path);

            if (oldExcluded && newExcluded) return;

            if (oldExcluded)
            {
                type = FileChangeType.Created;
                oldPath = null;
            }
            else if (newExcluded)
            {
                type = FileChangeType.Deleted;
                path = oldPath;
                oldPath = null;
            }
        }
        else if (ShouldExclude(path))
        {
            return;
        }

        var evt = new FileChangeEvent
        {
            ChangeType = type,
            FullPath = path,
            OldPath = oldPath,
            IsDirectory = isDirectory,
            Timestamp = DateTime.UtcNow
        };

        var generation = Volatile.Read(ref _generation);
        if (!_isWatching) return;

        _eventQueue.Enqueue((evt, generation));
    }

    private bool ShouldExclude(string path)
    {
        foreach (var pattern in _excludedPaths)
        {
            if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && _excludedExtensions.Contains(ext))
            return true;

        return false;
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        var pending = new LinkedList<(FileChangeEvent Event, long Generation)>();
        var pendingModified = new Dictionary<string, LinkedListNode<(FileChangeEvent Event, long Generation)>>(StringComparer.OrdinalIgnoreCase);
        var structuralPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var pendingGeneration = Volatile.Read(ref _generation);
        var lastProcessTime = DateTime.UtcNow;

        void ResetPending(long generation)
        {
            pending.Clear();
            pendingModified.Clear();
            structuralPaths.Clear();
            pendingGeneration = generation;
            lastProcessTime = DateTime.UtcNow;
        }

        void RemovePendingModification(string path)
        {
            if (pendingModified.Remove(path, out var node))
            {
                pending.Remove(node);
            }
        }

        try
        {
            while (!ct.IsCancellationRequested)
            {
                var currentGeneration = Volatile.Read(ref _generation);
                if (currentGeneration != pendingGeneration)
                {
                    ResetPending(currentGeneration);
                }

                while (_eventQueue.TryDequeue(out var queued))
                {
                    currentGeneration = Volatile.Read(ref _generation);
                    if (currentGeneration != pendingGeneration)
                    {
                        ResetPending(currentGeneration);
                    }

                    if (queued.Generation != currentGeneration)
                        continue;

                    var evt = queued.Event;

                    if (evt.ChangeType == FileChangeType.Modified)
                    {
                        if (structuralPaths.Contains(evt.FullPath))
                            continue;

                        if (pendingModified.TryGetValue(evt.FullPath, out var existingModification))
                        {
                            pending.Remove(existingModification);
                            pendingModified[evt.FullPath] = pending.AddLast(queued);
                        }
                        else
                        {
                            pendingModified[evt.FullPath] = pending.AddLast(queued);
                        }
                    }
                    else
                    {
                        RemovePendingModification(evt.FullPath);
                        structuralPaths.Add(evt.FullPath);

                        if (evt.ChangeType == FileChangeType.Renamed && evt.OldPath != null)
                        {
                            RemovePendingModification(evt.OldPath);
                            structuralPaths.Add(evt.OldPath);
                        }

                        pending.AddLast(queued);
                    }
                }

                var elapsed = (DateTime.UtcNow - lastProcessTime).TotalMilliseconds;
                if (elapsed >= _debounceMs && pending.Count > 0)
                {
                    foreach (var queued in pending)
                    {
                        var shouldDispatch = false;
                        lock (_stateLock)
                        {
                            if (_isWatching && queued.Generation == Volatile.Read(ref _generation))
                            {
                                _inFlightCallbacks++;
                                shouldDispatch = true;
                            }
                        }

                        if (!shouldDispatch)
                            continue;

                        var previousDispatchingService = _dispatchingService;
                        try
                        {
                            _dispatchingService = this;
                            OnChange?.Invoke(queued.Event);
                        }
                        catch (Exception ex)
                        {
                            NotifyError(ex);
                        }
                        finally
                        {
                            _dispatchingService = previousDispatchingService;
                            lock (_stateLock)
                            {
                                _inFlightCallbacks--;
                                Monitor.PulseAll(_stateLock);
                            }
                        }
                    }

                    ResetPending(Volatile.Read(ref _generation));
                }

                await Task.Delay(50, ct).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            lock (_stateLock)
            {
                if (!_disposed)
                {
                    _isWatching = false;
                    Interlocked.Increment(ref _generation);
                    _processorTask = null;
                    foreach (var watcher in _watchers)
                    {
                        try { watcher.EnableRaisingEvents = false; }
                        catch { }
                    }
                }
            }

            var previousDispatchingService = _dispatchingService;
            try
            {
                _dispatchingService = this;
                NotifyError(ex);
            }
            finally
            {
                _dispatchingService = previousDispatchingService;
            }
        }
    }

    #endregion

    #region Helpers

    private static bool IsDirectory(string path)
    {
        try
        {
            return Directory.Exists(path);
        }
        catch
        {
            return false;
        }
    }

    public void TriggerEvent(FileChangeEvent evt)
    {
        var generation = Volatile.Read(ref _generation);
        if (!_isWatching) return;

        _eventQueue.Enqueue((evt, generation));
    }

    internal void TriggerError(Exception exception)
    {
        NotifyError(exception);
    }

    public void ClearPendingEvents()
    {
        lock (_stateLock)
        {
            Interlocked.Increment(ref _generation);
            while (_eventQueue.TryDequeue(out _)) { }
        }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        Task? processorTask;
        var disposingFromProcessor = ReferenceEquals(_dispatchingService, this);

        lock (_stateLock)
        {
            while (_isClearing && !disposingFromProcessor)
            {
                Monitor.Wait(_stateLock);
            }

            if (_disposed) return;

            if (!_isClearing)
            {
                foreach (var watcher in _watchers)
                {
                    watcher.EnableRaisingEvents = false;
                    watcher.Dispose();
                }
                _watchers.Clear();
            }

            _isWatching = false;
            Interlocked.Increment(ref _generation);

            _cts.Cancel();
            processorTask = _processorTask;
            _disposed = true;
        }

        if (processorTask != null && !disposingFromProcessor)
        {
            try
            {
                processorTask.GetAwaiter().GetResult();
            }
            catch (OperationCanceledException)
            {
            }
        }

        if (processorTask != null && disposingFromProcessor)
        {
            _ = processorTask.ContinueWith(
                _ => _cts.Dispose(),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default);
        }
        else
        {
            _cts.Dispose();
        }
        GC.SuppressFinalize(this);
    }

    private void NotifyError(Exception exception)
    {
        try
        {
            OnError?.Invoke(exception);
        }
        catch
        {
        }
    }

    private static string NormalizeDirectoryPath(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        return string.Equals(fullPath, root, StringComparison.OrdinalIgnoreCase)
            ? fullPath
            : fullPath.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
    }

    private void ThrowIfDisposed()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
    }

    #endregion
}
