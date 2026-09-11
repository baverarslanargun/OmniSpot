namespace SmartFileLauncher.UI.Services;

internal sealed class SingleInstanceCoordinator : IDisposable
{
    private const string MutexName = @"Local\OmniSpot.SingleInstance";
    private const string ActivationEventName = @"Local\OmniSpot.Activate";
    private const string ShutdownEventName = @"Local\OmniSpot.Shutdown";

    private readonly Mutex _mutex;
    private readonly EventWaitHandle _activationEvent;
    private readonly EventWaitHandle _shutdownEvent;
    private RegisteredWaitHandle? _activationRegistration;
    private RegisteredWaitHandle? _shutdownRegistration;
    private bool _ownsMutex;
    private bool _disposed;

    internal SingleInstanceCoordinator(string? instanceId = null)
    {
        var suffix = string.IsNullOrWhiteSpace(instanceId) ? string.Empty : $".{instanceId}";
        _activationEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ActivationEventName + suffix);
        _shutdownEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShutdownEventName + suffix);
        _mutex = new Mutex(true, MutexName + suffix, out var createdNew);
        _ownsMutex = createdNew;
    }

    internal bool IsPrimary => _ownsMutex;

    internal void StartListening(Action activationRequested, Action shutdownRequested)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(activationRequested);
        ArgumentNullException.ThrowIfNull(shutdownRequested);

        if (!IsPrimary)
        {
            throw new InvalidOperationException("Yalnız birincil OmniSpot örneği istek dinleyebilir.");
        }

        _activationRegistration = ThreadPool.RegisterWaitForSingleObject(
            _activationEvent,
            static (state, timedOut) =>
            {
                if (!timedOut)
                {
                    ((Action)state!).Invoke();
                }
            },
            activationRequested,
            Timeout.Infinite,
            executeOnlyOnce: false);

        _shutdownRegistration = ThreadPool.RegisterWaitForSingleObject(
            _shutdownEvent,
            static (state, timedOut) =>
            {
                if (!timedOut)
                {
                    ((Action)state!).Invoke();
                }
            },
            shutdownRequested,
            Timeout.Infinite,
            executeOnlyOnce: false);
    }

    internal void SignalActivation()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _activationEvent.Set();
    }

    internal bool SignalShutdownAndWait(TimeSpan timeout)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        if (IsPrimary)
        {
            return true;
        }

        _shutdownEvent.Set();

        try
        {
            if (!_mutex.WaitOne(timeout))
            {
                return false;
            }
        }
        catch (AbandonedMutexException)
        {
        }

        _mutex.ReleaseMutex();
        return true;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _activationRegistration?.Unregister(null);
        _shutdownRegistration?.Unregister(null);
        _activationEvent.Dispose();
        _shutdownEvent.Dispose();

        if (_ownsMutex)
        {
            _mutex.ReleaseMutex();
            _ownsMutex = false;
        }

        _mutex.Dispose();
    }
}
