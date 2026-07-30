using System.Net.NetworkInformation;

namespace SmartFileLauncher.Core.Application.Connectivity;

public sealed class ConnectivityMonitor : IConnectivityMonitor
{
    private readonly Func<CancellationToken, Task<bool>> _probe;
    private readonly TimeSpan _interval;
    private readonly object _lifecycleLock = new();
    private readonly SemaphoreSlim _checkGate = new(1, 1);
    private CancellationTokenSource? _monitorCancellation;
    private Task? _monitorTask;
    private volatile bool _isConnected = true;
    private int _disposed;

    public ConnectivityMonitor(TimeSpan? interval = null)
        : this(ProbeAsync, interval ?? TimeSpan.FromSeconds(10))
    {
    }

    public ConnectivityMonitor(
        Func<CancellationToken, Task<bool>> probe,
        TimeSpan interval)
    {
        _probe = probe ?? throw new ArgumentNullException(nameof(probe));
        if (interval <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(nameof(interval));
        }

        _interval = interval;
    }

    public bool IsConnected => _isConnected;

    public event Action<bool>? ConnectivityChanged;

    public async Task<bool> CheckNowAsync(
        CancellationToken cancellationToken = default)
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        await _checkGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            bool isConnected;
            try
            {
                isConnected = await _probe(cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (OperationCanceledException)
                when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                isConnected = false;
            }

            var changed = isConnected != _isConnected;
            _isConnected = isConnected;
            if (changed)
            {
                ConnectivityChanged?.Invoke(isConnected);
            }

            return isConnected;
        }
        finally
        {
            _checkGate.Release();
        }
    }

    public void Start()
    {
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        lock (_lifecycleLock)
        {
            if (_monitorCancellation != null)
            {
                return;
            }

            _monitorCancellation = new CancellationTokenSource();
            _monitorTask = MonitorAsync(_monitorCancellation.Token);
        }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        CancellationTokenSource? cancellation;
        lock (_lifecycleLock)
        {
            cancellation = _monitorCancellation;
            _monitorCancellation = null;
            _monitorTask = null;
        }

        cancellation?.Cancel();
        cancellation?.Dispose();
    }

    private async Task MonitorAsync(CancellationToken cancellationToken)
    {
        using var timer = new PeriodicTimer(_interval);
        try
        {
            while (await timer.WaitForNextTickAsync(cancellationToken)
                       .ConfigureAwait(false))
            {
                await CheckNowAsync(cancellationToken).ConfigureAwait(false);
            }
        }
        catch (OperationCanceledException)
            when (cancellationToken.IsCancellationRequested)
        {
        }
        catch (ObjectDisposedException)
            when (Volatile.Read(ref _disposed) != 0)
        {
        }
    }

    private static async Task<bool> ProbeAsync(
        CancellationToken cancellationToken)
    {
        if (!NetworkInterface.GetIsNetworkAvailable())
        {
            return false;
        }

        using var ping = new Ping();
        var reply = await ping.SendPingAsync("8.8.8.8", 1000)
            .WaitAsync(cancellationToken)
            .ConfigureAwait(false);
        return reply.Status == IPStatus.Success;
    }
}
