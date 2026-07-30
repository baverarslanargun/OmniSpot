namespace SmartFileLauncher.Core.Application.Connectivity;

public interface IConnectivityMonitor : IDisposable
{
    bool IsConnected { get; }
    event Action<bool>? ConnectivityChanged;
    Task<bool> CheckNowAsync(CancellationToken cancellationToken = default);
    void Start();
}
