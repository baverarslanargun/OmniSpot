using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Application.Indexing;

public sealed class IndexLifecycleService : IIndexLifecycleService
{
    private readonly IndexManager _indexManager;
    private readonly IIndexedLocationProvider _locationProvider;
    private readonly ChangeFeedIndexBridge? _changeFeed;
    private readonly TimeSpan _renewInterval;
    private readonly CancellationTokenSource _leaseCancellation = new();
    private readonly SemaphoreSlim _leaseGate = new(1, 1);
    private Task? _leaseTask;
    private int _handedOver;
    private int _leaseReleaseConfirmed;
    private bool _disposed;

    public bool LeaseReleaseConfirmed => Volatile.Read(ref _leaseReleaseConfirmed) != 0;

    public IndexLifecycleService(
        IndexManager indexManager,
        IIndexedLocationProvider locationProvider,
        ChangeFeedIndexBridge? changeFeed = null,
        TimeSpan? renewInterval = null)
    {
        _indexManager = indexManager ?? throw new ArgumentNullException(nameof(indexManager));
        _locationProvider = locationProvider ??
            throw new ArgumentNullException(nameof(locationProvider));
        _changeFeed = changeFeed;
        _renewInterval = renewInterval ?? ChangeFeedIndexBridge.DefaultRenewInterval;

        if (_changeFeed is not null)
        {
            _indexManager.OnWatcherFault += HandOverAfterWatcherFault;
        }

        if (_renewInterval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(renewInterval));
        }
    }

    public ChangeFeedAdoptionResult? LastAdoption { get; private set; }

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

    public event Action<string>? Notice;

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
        await _indexManager.InitializeWithWatcherFenceAsync(
                locations.RootPaths,
                cancellationToken,
                AdoptChangeFeedAsync)
            .ConfigureAwait(false);

        return new IndexStartupResult(
            locations.DesktopPath,
            locations.RootPaths,
            _indexManager.GetStats());
    }

    internal static bool CoversDowntime(ChangeFeedAdoptionResult? adoption, int rootCount) =>
        adoption is { LeaseHeld: true } &&
        adoption.Status is ChangeFeedAdoptionStatus.Adopted
            or ChangeFeedAdoptionStatus.NothingToAdopt &&
        rootCount > 0 &&
        adoption.RootsSubscribed == rootCount;

    internal static string DescribeAdoption(ChangeFeedAdoptionResult? adoption)
    {
        if (adoption is null)
        {
            return "Değişiklik akışı devralınamadı; sonuç yok.";
        }

        var kira = adoption.LeaseHeld ? "alındı" : "ALINMADI";
        var ek = string.IsNullOrWhiteSpace(adoption.Diagnostics)
            ? string.Empty
            : " " + adoption.Diagnostics;

        return $"Değişiklik akışı devralma: {adoption.Status} kök={adoption.RootsSubscribed} " +
            $"sayfa={adoption.PagesApplied} olay={adoption.EventsApplied} " +
            $"yeniden={adoption.RootsResynchronized} kira={kira}{ek}";
    }

    private void ReportCoverage(int rootCount)
    {
        var coversDowntime = CoversDowntime(LastAdoption, rootCount);
        _indexManager.NoteChangeFeedCoverage(coversDowntime);

        Notice?.Invoke(
            DescribeAdoption(LastAdoption) +
            (coversDowntime
                ? " | açılış taraması atlandı"
                : " | açılış taraması yapılacak"));
    }

    private async Task<bool> AdoptChangeFeedAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken)
    {
        if (_changeFeed is null)
        {
            return false;
        }

        await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        var watcherPrepared = false;
        try
        {
            watcherPrepared = _indexManager.BeginWatcherCaptureWithinLifecycle(roots);
            LastAdoption = await _changeFeed
                .AdoptAsync(roots, cancellationToken, withinLifecycle: true)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception failure)
        {
            LastAdoption = null;
            _indexManager.NotifyExternalError(
                $"Değişiklik akışı devralınamadı: {failure.Message}");
            ReportCoverage(roots.Count);
            return watcherPrepared;
        }
        finally
        {
            _leaseGate.Release();
        }

        ReportCoverage(roots.Count);

        if (LastAdoption?.LeaseHeld == true)
        {
            Interlocked.Exchange(ref _leaseReleaseConfirmed, 0);
        }

        if (LastAdoption?.LeaseHeld != true || Volatile.Read(ref _handedOver) != 0)
        {
            return watcherPrepared;
        }

        _leaseTask = Task.Run(
            () => RenewLeaseAsync(_leaseCancellation.Token),
            CancellationToken.None);

        return watcherPrepared;
    }

    private async Task RenewLeaseAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_renewInterval);

        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken).ConfigureAwait(false))
            {
                if (_changeFeed is null)
                {
                    return;
                }

                await _leaseGate.WaitAsync(cancellationToken).ConfigureAwait(false);
                bool renewed;
                try
                {
                    if (Volatile.Read(ref _handedOver) != 0)
                    {
                        return;
                    }

                    renewed = await _changeFeed
                        .RenewLeaseAsync(cancellationToken)
                        .ConfigureAwait(false);
                }
                finally
                {
                    _leaseGate.Release();
                }

                if (!renewed)
                {
                    Interlocked.Exchange(ref _handedOver, 1);
                    await ReleaseWithRetryAsync(cancellationToken).ConfigureAwait(false);
                    _indexManager.NotifyExternalError(
                        "Watcher kirası yenilenemedi; değişiklik akışı servise bırakıldı.");
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
        }
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

    private async Task<bool> ReleaseWithRetryAsync(CancellationToken cancellationToken)
    {
        if (_changeFeed is null)
        {
            return false;
        }

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(HandOverBudget);

        for (var attempt = 0; attempt < ReleaseAttempts; attempt++)
        {
            try
            {
                if (await _changeFeed.HandOverAsync(deadline.Token).ConfigureAwait(false))
                {
                    Interlocked.Exchange(ref _leaseReleaseConfirmed, 1);
                    return true;
                }
            }
            catch (Exception)
            {
            }
        }

        _indexManager.NotifyExternalError(
            "Watcher kirası bırakıldığı doğrulanamadı; servis kira süresi dolana kadar pasif kalabilir.");

        return false;
    }

    private const int ReleaseAttempts = 2;

    private void HandOverAfterWatcherFault()
    {
        if (_changeFeed is null ||
            _disposed ||
            Interlocked.Exchange(ref _handedOver, 1) != 0)
        {
            return;
        }

        _leaseCancellation.Cancel();

        WatcherFaultHandOver = Task.Run(async () =>
        {
            try
            {
                using var deadline = new CancellationTokenSource(HandOverBudget);
                await DrainLeaseTaskAsync(deadline.Token).ConfigureAwait(false);

                await _leaseGate.WaitAsync(deadline.Token).ConfigureAwait(false);
                try
                {
                    await ReleaseWithRetryAsync(deadline.Token).ConfigureAwait(false);
                }
                finally
                {
                    _leaseGate.Release();
                }
            }
            catch (Exception failure)
            {
                _indexManager.NotifyExternalError(
                    $"Watcher hatasında devir tamamlanamadı: {failure.Message}");
            }
        });
    }

    internal Task? WatcherFaultHandOver { get; private set; }

    private async Task DrainLeaseTaskAsync(CancellationToken cancellationToken)
    {
        var running = _leaseTask;
        if (running is null)
        {
            return;
        }

        try
        {
            await running.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception)
        {
        }
    }

    private void HandOverChangeFeed()
    {
        _leaseCancellation.Cancel();

        if (_changeFeed is null)
        {
            return;
        }

        try
        {
            using var deadline = new CancellationTokenSource(HandOverBudget);
            DrainLeaseTaskAsync(deadline.Token).GetAwaiter().GetResult();

            _leaseGate.Wait(deadline.Token);

            try
            {
                ReleaseWithRetryAsync(deadline.Token).GetAwaiter().GetResult();
            }
            finally
            {
                _leaseGate.Release();
            }
        }
        catch (Exception)
        {
        }
    }

    private static TimeSpan HandOverBudget => TimeSpan.FromSeconds(2);

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;

        if (_changeFeed is not null)
        {
            _indexManager.OnWatcherFault -= HandOverAfterWatcherFault;
        }

        HandOverChangeFeed();
        _leaseGate.Dispose();
        _leaseCancellation.Dispose();
        _indexManager.Dispose();
    }
}
