using System.Collections.Concurrent;

namespace SmartFileLauncher.Core.Indexing.Folders;

internal sealed class FolderVolumeWatcher : IDisposable
{
    private const int DirtyLimit = 100_000;
    private readonly List<FileSystemWatcher> _watchers = [];
    private readonly ConcurrentDictionary<string, byte> _dirty = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(string Root, string Reason)> _unwatched = [];
    private int _overflowed;
    private bool _disposed;

    internal FolderVolumeWatcher(IReadOnlyList<string> roots)
    {
        foreach (var root in roots)
        {
            FileSystemWatcher? watcher = null;
            try
            {
                watcher = new FileSystemWatcher(root)
                {
                    IncludeSubdirectories = true,
                    InternalBufferSize = 64 * 1024,
                    NotifyFilter = NotifyFilters.FileName | NotifyFilters.DirectoryName | NotifyFilters.Size |
                        NotifyFilters.LastWrite | NotifyFilters.CreationTime | NotifyFilters.Attributes
                };
                watcher.Created += (_, change) => Touch(change.FullPath);
                watcher.Deleted += (_, change) => Touch(change.FullPath);
                watcher.Changed += (_, change) => Touch(change.FullPath);
                watcher.Renamed += (_, change) =>
                {
                    Touch(change.OldFullPath);
                    Touch(change.FullPath);
                };
                watcher.Error += (_, _) => Interlocked.Exchange(ref _overflowed, 1);
                watcher.EnableRaisingEvents = true;
                _watchers.Add(watcher);
            }
            catch (Exception failure) when (failure is ArgumentException or IOException or UnauthorizedAccessException or
                PlatformNotSupportedException)
            {
                watcher?.Dispose();
                _unwatched.Add((root, failure.Message));
            }
        }
    }

    internal IReadOnlyList<(string Root, string Reason)> Unwatched => _unwatched;

    internal void Touch(string path)
    {
        if (Volatile.Read(ref _disposed)) return;
        var parent = FolderVolumeScanner.Parent(path);
        if (parent is null) return;
        if (_dirty.Count >= DirtyLimit)
        {
            Interlocked.Exchange(ref _overflowed, 1);
            return;
        }
        _dirty.TryAdd(parent, 0);
    }

    internal IReadOnlyCollection<string> TakeDirty()
    {
        if (_dirty.IsEmpty) return [];
        var taken = new List<string>(_dirty.Count);
        foreach (var path in _dirty.Keys)
            if (_dirty.TryRemove(path, out _)) taken.Add(path);
        return taken;
    }

    internal bool TakeOverflow() => Interlocked.Exchange(ref _overflowed, 0) != 0;

    public void Dispose()
    {
        if (_disposed) return;
        Volatile.Write(ref _disposed, true);
        foreach (var watcher in _watchers)
        {
            watcher.EnableRaisingEvents = false;
            watcher.Dispose();
        }
        _watchers.Clear();
        _dirty.Clear();
    }
}
