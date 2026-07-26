using System;
using System.Collections.Concurrent;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Services;

/// <summary>
/// FileSystemWatcher wrapper that buffers events in a ConcurrentQueue.
/// Provides debouncing to handle rapid successive changes.
/// 
/// Data Structures Used:
/// - ConcurrentQueue: O(1) thread-safe enqueue/dequeue for event buffering
/// - HashSet: O(1) lookup for excluded paths
/// </summary>
public class FileWatcherService : IDisposable
{
    private readonly ConcurrentQueue<FileChangeEvent> _eventQueue;
    private readonly HashSet<string> _excludedPaths;
    private readonly HashSet<string> _excludedExtensions;
    private readonly List<FileSystemWatcher> _watchers;
    private readonly CancellationTokenSource _cts;
    private readonly int _debounceMs;
    private bool _disposed;
    private bool _isWatching;

    /// <summary>
    /// Fired when a file system change is detected (after debouncing).
    /// </summary>
    public event Action<FileChangeEvent>? OnChange;

    /// <summary>
    /// Fired when an error occurs in a watcher.
    /// </summary>
    public event Action<Exception>? OnError;

    public FileWatcherService(int debounceMs = 100)
    {
        _eventQueue = new ConcurrentQueue<FileChangeEvent>();
        _excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _excludedExtensions = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        _watchers = new List<FileSystemWatcher>();
        _cts = new CancellationTokenSource();
        _debounceMs = debounceMs;

        // Default excluded paths
        AddExcludedPath(@"\$Recycle.Bin\");
        AddExcludedPath(@"\System Volume Information\");
        AddExcludedPath(@"\.git\");
        AddExcludedPath(@"\node_modules\");
        AddExcludedPath(@"\bin\");
        AddExcludedPath(@"\obj\");
        AddExcludedPath(@"\__pycache__\");
        
        // Default excluded extensions (temp files)
        AddExcludedExtension(".tmp");
        AddExcludedExtension(".temp");
        AddExcludedExtension(".partial");
        AddExcludedExtension(".crdownload");
    }

    /// <summary>
    /// Number of pending events in the queue.
    /// </summary>
    public int PendingEventCount => _eventQueue.Count;

    /// <summary>
    /// Whether the service is currently watching.
    /// </summary>
    public bool IsWatching => _isWatching;

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

    /// <summary>
    /// Starts watching the specified path.
    /// </summary>
    public void Watch(string path)
    {
        if (!Directory.Exists(path))
            throw new DirectoryNotFoundException($"Directory not found: {path}");

        var watcher = new FileSystemWatcher(path)
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
    }

    /// <summary>
    /// Starts all watchers and begins processing events.
    /// </summary>
    public void Start()
    {
        if (_isWatching) return;

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = true;
        }

        _isWatching = true;

        // Start the event processor
        Task.Run(() => ProcessEventsAsync(_cts.Token));
    }

    /// <summary>
    /// Stops all watchers.
    /// </summary>
    public void Stop()
    {
        if (!_isWatching) return;

        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
        }

        _isWatching = false;
    }

    #endregion

    #region Event Handlers

    private void OnFileCreated(object sender, FileSystemEventArgs e)
    {
        EnqueueEvent(FileChangeType.Created, e.FullPath, null, IsDirectory(e.FullPath));
    }

    private void OnFileDeleted(object sender, FileSystemEventArgs e)
    {
        // Can't check IsDirectory for deleted items, assume based on extension
        bool isDir = string.IsNullOrEmpty(Path.GetExtension(e.FullPath));
        EnqueueEvent(FileChangeType.Deleted, e.FullPath, null, isDir);
    }

    private void OnFileRenamed(object sender, RenamedEventArgs e)
    {
        EnqueueEvent(FileChangeType.Renamed, e.FullPath, e.OldFullPath, IsDirectory(e.FullPath));
    }

    private void OnFileChanged(object sender, FileSystemEventArgs e)
    {
        // Only track file changes, not directory changes
        if (!IsDirectory(e.FullPath))
        {
            EnqueueEvent(FileChangeType.Modified, e.FullPath, null, false);
        }
    }

    private void OnWatcherError(object sender, ErrorEventArgs e)
    {
        OnError?.Invoke(e.GetException());
    }

    #endregion

    #region Event Processing

    private void EnqueueEvent(FileChangeType type, string path, string? oldPath, bool isDirectory)
    {
        // Filter excluded paths
        if (ShouldExclude(path)) return;
        if (oldPath != null && ShouldExclude(oldPath)) return;

        var evt = new FileChangeEvent
        {
            ChangeType = type,
            FullPath = path,
            OldPath = oldPath,
            IsDirectory = isDirectory,
            Timestamp = DateTime.UtcNow
        };

        _eventQueue.Enqueue(evt);
    }

    private bool ShouldExclude(string path)
    {
        // Check path patterns
        foreach (var pattern in _excludedPaths)
        {
            if (path.Contains(pattern, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        // Check extensions
        var ext = Path.GetExtension(path);
        if (!string.IsNullOrEmpty(ext) && _excludedExtensions.Contains(ext))
            return true;

        return false;
    }

    private async Task ProcessEventsAsync(CancellationToken ct)
    {
        // Debounce dictionary: path -> last event
        var pending = new Dictionary<string, FileChangeEvent>(StringComparer.OrdinalIgnoreCase);
        var lastProcessTime = DateTime.UtcNow;

        while (!ct.IsCancellationRequested)
        {
            // Drain queue into pending dictionary (deduplication)
            while (_eventQueue.TryDequeue(out var evt))
            {
                // For renamed events, remove old path entry
                if (evt.ChangeType == FileChangeType.Renamed && evt.OldPath != null)
                {
                    pending.Remove(evt.OldPath);
                }

                // Use the latest event for each path
                pending[evt.FullPath] = evt;
            }

            // Check if debounce period has passed
            var elapsed = (DateTime.UtcNow - lastProcessTime).TotalMilliseconds;
            if (elapsed >= _debounceMs && pending.Count > 0)
            {
                // Process all pending events
                foreach (var evt in pending.Values)
                {
                    try
                    {
                        OnChange?.Invoke(evt);
                    }
                    catch (Exception ex)
                    {
                        OnError?.Invoke(ex);
                    }
                }

                pending.Clear();
                lastProcessTime = DateTime.UtcNow;
            }

            await Task.Delay(50, ct).ConfigureAwait(false);
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

    /// <summary>
    /// Manually trigger an event (useful for testing or forced updates).
    /// </summary>
    public void TriggerEvent(FileChangeEvent evt)
    {
        _eventQueue.Enqueue(evt);
    }

    /// <summary>
    /// Clear all pending events.
    /// </summary>
    public void ClearPendingEvents()
    {
        while (_eventQueue.TryDequeue(out _)) { }
    }

    #endregion

    #region IDisposable

    public void Dispose()
    {
        if (!_disposed)
        {
            _cts.Cancel();
            Stop();

            foreach (var watcher in _watchers)
            {
                watcher.Dispose();
            }
            _watchers.Clear();

            _cts.Dispose();
            _disposed = true;
        }
        GC.SuppressFinalize(this);
    }

    ~FileWatcherService()
    {
        Dispose();
    }

    #endregion
}
