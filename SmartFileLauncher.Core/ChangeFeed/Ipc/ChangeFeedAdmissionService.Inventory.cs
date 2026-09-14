using System.IO.Pipes;
using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed partial class ChangeFeedAdmissionService
{
    private ChangeFeedResponse Inventory(NamedPipeServerStream pipe, ChangeFeedRequest request,
        CancellationToken cancellationToken)
    {
        if (_inventory is null)
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, "MFT envanteri kullanılamıyor.");
        var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);
        if (request.Kind == ChangeFeedRequestKind.ValidateInventory && request.Token is null)
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "Envanter oturumu belirtilmedi.");
        if (request.Kind == ChangeFeedRequestKind.CancelInventory)
        {
            _inventory.Cancel(caller.Value, request.Token);
            return ChangeFeedResponse.Ok();
        }
        var store = _storeFactory(caller.Value);
        using var scope = store.EnterOwnerScope(cancellationToken);
        var subscription = store.ReadSubscription();
        if (subscription is null || subscription.OwnerSid != caller.Value)
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.NoSubscription, "Abonelik yok.");

        var requested = request.Token is null
            ? request.InventoryRoots
            : _inventory.Roots(caller.Value, request.Token)?.Select(root => root.RootPath).ToArray();
        if (requested is null || requested.Count == 0 || requested.Count > ChangeFeedSubscription.MaximumRoots)
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "Envanter kökleri geçersiz.");
        var roots = new List<ChangeFeedSubscribedRoot>();
        foreach (var path in requested)
        {
            var root = subscription.Roots.SingleOrDefault(candidate => PathsMatch(candidate.RootPath, path));
            if (root is null || roots.Contains(root))
                return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.RootUnauthorized, "Kök aboneliği geçersiz.");
            var (verified, decision) = _admission.Evaluate(pipe, path);
            if (!verified.Equals(caller) || decision.Status != ChangeFeedResponseStatus.Ok ||
                decision.Identity != root.Identity)
                return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.RootUnauthorized, "Kök erişimi veya kimliği değişti.");
            roots.Add(root);
        }
        var binding = new ChangeFeedChainBinding(caller.Value, ChangeFeedProtocol.Version,
            store.ReadEpoch(), store.ReadSecurityStamp(), subscription.Roots);
        try
        {
            var token = request.Token ?? _inventory.Start(binding, roots);
            return ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid =>
            {
                if (!sid.Equals(caller))
                    throw new ChangeFeedImpersonationException("Çağıranın kimliği değişti.");
                var authorizers = roots.Select(root => _authorizerFactory(root.RootPath)).ToArray();
                return _inventory.Page(binding, token,
                    path => authorizers.Any(authorizer => authorizer.CanReturnExistingPath(path)),
                    request.Kind == ChangeFeedRequestKind.ValidateInventory, cancellationToken);
            });
        }
        catch (Exception failure) when (failure is IOException or NotSupportedException)
        {
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, "MFT envanteri başlatılamadı.");
        }
    }
}
