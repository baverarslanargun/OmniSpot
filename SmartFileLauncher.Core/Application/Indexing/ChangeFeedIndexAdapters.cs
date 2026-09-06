using System.Runtime.Versioning;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Application.Indexing;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedClientChannel : IChangeFeedRequestChannel
{
    private readonly ChangeFeedClient _client;

    public ChangeFeedClientChannel(ChangeFeedClient? client = null)
    {
        _client = client ?? new ChangeFeedClient();
    }

    public Task<ChangeFeedResponse> SendAsync(
        ChangeFeedRequest request,
        CancellationToken cancellationToken) =>
        _client.SendAsync(request, cancellationToken);
}

public sealed class IndexManagerChangeFeedTarget : IChangeFeedIndexTarget
{
    private readonly IndexManager _manager;

    public IndexManagerChangeFeedTarget(IndexManager manager)
    {
        _manager = manager ?? throw new ArgumentNullException(nameof(manager));
    }

    public bool Apply(IReadOnlyList<FileChangeEvent> changes) =>
        _manager.ApplyExternalChanges(changes);

    public Task<bool> ResynchronizeAsync(
        string rootPath,
        bool withinLifecycle,
        CancellationToken cancellationToken) =>
        withinLifecycle
            ? _manager.ReconcileWithinLifecycleAsync(rootPath, cancellationToken)
            : _manager.EnsureSyncedAsync(rootPath, cancellationToken);
}
