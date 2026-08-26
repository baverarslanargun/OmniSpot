using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Application.Indexing;

public sealed class IndexLifecycleService : IIndexLifecycleService
{
    private readonly IndexManager _indexManager;
    private readonly IIndexedLocationProvider _locationProvider;
    private bool _disposed;

    public IndexLifecycleService(
        IndexManager indexManager,
        IIndexedLocationProvider locationProvider)
    {
        _indexManager = indexManager ?? throw new ArgumentNullException(nameof(indexManager));
        _locationProvider = locationProvider ??
            throw new ArgumentNullException(nameof(locationProvider));
    }

    public event Action<IndexProgress>? ProgressChanged
    {
        add => _indexManager.OnProgress += value;
        remove => _indexManager.OnProgress -= value;
    }

    public event Action<FileChangeEvent>? FileChanged
    {
        add => _indexManager.OnFileChange += value;
        remove => _indexManager.OnFileChange -= value;
    }

    public event Action<string>? Error
    {
        add => _indexManager.OnError += value;
        remove => _indexManager.OnError -= value;
    }

    public event Action<int, int, int>? ReconciliationProgressChanged
    {
        add => _indexManager.OnDeltaSyncProgress += value;
        remove => _indexManager.OnDeltaSyncProgress -= value;
    }

    public event Action<bool>? ReconciliationStateChanged
    {
        add => _indexManager.OnDeltaSyncStateChanged += value;
        remove => _indexManager.OnDeltaSyncStateChanged -= value;
    }

    public bool IsInitialized => _indexManager.IsInitialized;
    public string DatabasePath => _indexManager.DatabasePath;
    public IndexReconciliationStatus ReconciliationStatus =>
        new(
            _indexManager.IsDeltaSyncRunning,
            _indexManager.DeltaSyncProgress,
            _indexManager.DeltaSyncProcessed,
            _indexManager.DeltaSyncTotal);

    public IndexDiagnosticsReport GetDiagnosticsReport()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _indexManager.GetDiagnosticsReport();
    }

    public async Task<IndexStartupResult> InitializeAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var locations = _locationProvider.Resolve();
        await _indexManager.InitializeAsync(locations.RootPaths, cancellationToken)
            .ConfigureAwait(false);

        return new IndexStartupResult(
            locations.DesktopPath,
            locations.RootPaths,
            _indexManager.GetStats());
    }

    public Task<bool> EnsureSyncedAsync(
        string path,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _indexManager.EnsureSyncedAsync(path, cancellationToken);
    }

    public IReadOnlyList<FileSystemNode> GetIndexedRoots(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        cancellationToken.ThrowIfCancellationRequested();
        return _indexManager.RootNode?.Children ?? Array.Empty<FileSystemNode>();
    }

    public IndexTokenMatches GetTokenMatches(
        string token,
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var matches = _indexManager.CreateSearchState(cancellationToken).Get(token);
        return new IndexTokenMatches(
            matches.Count,
            matches.Take(3).Select(item => item.Name).ToArray());
    }

    public SearchState CreateSearchState(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _indexManager.CreateSearchState(cancellationToken);
    }

    public SearchSnapshot CreateSearchSnapshot(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _indexManager.CreateSearchSnapshot(cancellationToken);
    }

    public IndexStats GetStats()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        return _indexManager.GetStats();
    }

    public void RecordOpened(string path)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _indexManager.IncrementOpenCount(path);
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _indexManager.Dispose();
    }
}
