using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
public sealed partial class ChangeFeedAdmissionService : IDisposable
{
    private readonly ChangeFeedRootAdmission _admission;
    private readonly Func<string, IChangeFeedStore> _storeFactory;
    private readonly ChangeFeedDeliveryLedger _ledger;
    private readonly Func<string, ChangeFeedPathAuthorizer> _authorizerFactory;
    private readonly Func<string, CancellationToken, bool>? _handoffDrainer;
    private readonly TimeSpan _handoffDrainBudget;
    private readonly long _pageBudget;
    private readonly ChangeFeedInventorySessions? _inventory;
    private readonly SharedCatalogService? _catalogs;
    private readonly IndexVolumeService _volumes = new();
    internal Func<string, CancellationToken, SmartFileLauncher.Core.ChangeFeed.Usn.UsnDrainRunner.BootstrapLease>? CatalogBootstrap { get; }

    public ChangeFeedAdmissionService(
        ChangeFeedRootAdmission admission,
        Func<string, IChangeFeedStore> storeFactory,
        ChangeFeedDeliveryLedger? ledger = null,
        Func<string, ChangeFeedPathAuthorizer>? authorizerFactory = null,
        long pageBudget = ChangeFeedProtocol.MaximumPageBytes,
        Func<string, CancellationToken, bool>? handoffDrainer = null,
        TimeSpan? handoffDrainBudget = null,
        ChangeFeedInventorySessions? inventory = null,
        Func<string, ChangeFeedStoreLayout>? sharedCatalogLayout = null,
        Func<string, CancellationToken, SmartFileLauncher.Core.ChangeFeed.Usn.UsnDrainRunner.BootstrapLease>? catalogBootstrap = null)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
        _ledger = ledger ?? new ChangeFeedDeliveryLedger();
        _authorizerFactory = authorizerFactory ?? ChangeFeedPathAuthorizer.ForCurrentCaller;
        _pageBudget = pageBudget;
        _handoffDrainer = handoffDrainer;
        _inventory = inventory;
        if (sharedCatalogLayout is not null) _catalogs = new(this, sharedCatalogLayout, _storeFactory);
        CatalogBootstrap = catalogBootstrap;
        _handoffDrainBudget = handoffDrainBudget ?? ChangeFeedProtocol.HandoffDrainBudget;

        if (_handoffDrainBudget <= TimeSpan.Zero ||
            _handoffDrainBudget >= ChangeFeedProtocol.IoTimeout)
        {
            throw new ArgumentOutOfRangeException(nameof(handoffDrainBudget));
        }
    }

    public ChangeFeedResponse Handle(NamedPipeServerStream pipe, ChangeFeedRequest request,
        CancellationToken cancellationToken = default) =>
        Handle(ChangeFeedCaller.FromPipe(pipe), request, cancellationToken);
    internal ChangeFeedResponse Handle(
        ChangeFeedCaller pipe,
        ChangeFeedRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(request);

        if (request.Version != ChangeFeedProtocol.Version)
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.VersionMismatch,
                $"Desteklenen sürüm {ChangeFeedProtocol.Version}, gelen {request.Version}.");
        }

        try
        {
            return request.Kind switch
            {
                ChangeFeedRequestKind.AddRoot =>
                    AddRoot(pipe, request.RootPath, cancellationToken),
                ChangeFeedRequestKind.RemoveRoot =>
                    RemoveRoot(pipe, request.RootPath, cancellationToken),
                ChangeFeedRequestKind.ListRoots => ListRoots(pipe, cancellationToken),
                ChangeFeedRequestKind.Pull => Pull(pipe, request.Token, cancellationToken),
                ChangeFeedRequestKind.Acknowledge =>
                    Acknowledge(pipe, request.Token, cancellationToken),
                ChangeFeedRequestKind.HoldLease =>
                    HoldLease(pipe, request.LeaseSeconds, cancellationToken),
                ChangeFeedRequestKind.DrainAndHoldLease =>
                    DrainAndHoldLease(pipe, request.LeaseSeconds, cancellationToken),
                ChangeFeedRequestKind.ReleaseLease =>
                    ReleaseLease(pipe, cancellationToken),
                ChangeFeedRequestKind.GetCapabilities => ChangeFeedResponse.Granted("continuous-usn-v1") with
                    { Capabilities = ["shared-build-options-v1", "two-phase-index-v1", "volume-index-v1"] },
                ChangeFeedRequestKind.PrepareContinuousRoot => PrepareContinuousRoot(pipe, request.RootPath, cancellationToken),
                ChangeFeedRequestKind.DrainContinuous => DrainContinuous(pipe, cancellationToken),
                ChangeFeedRequestKind.Inventory or ChangeFeedRequestKind.ValidateInventory or
                    ChangeFeedRequestKind.CancelInventory => Inventory(pipe, request, cancellationToken),
                ChangeFeedRequestKind.OpenCatalog or ChangeFeedRequestKind.PollCatalog or
                    ChangeFeedRequestKind.RecordCatalogOpen or ChangeFeedRequestKind.RepairCatalog or ChangeFeedRequestKind.SynchronizeCatalog or ChangeFeedRequestKind.CloseCatalog =>
                    _catalogs?.Handle(pipe, request) ?? ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, "Ortak katalog hizmeti etkin değil."),
                ChangeFeedRequestKind.IndexVolumeOpen or ChangeFeedRequestKind.IndexVolumeScan or ChangeFeedRequestKind.IndexVolumeChanges =>
                    _volumes.Handle(pipe, request, cancellationToken),
                _ => ChangeFeedResponse.Failed(
                    ChangeFeedResponseStatus.InvalidRequest,
                    "Bilinmeyen istek türü.")
            };
        }
        catch (InvalidDataException)
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.Unavailable,
                "Mevcut abonelik kaydı okunamıyor; istek uygulanmadı.");
        }
    }

    internal ChangeFeedResponse HandleCatalog(ChangeFeedCaller caller, ChangeFeedRequest request, bool allNames, CancellationToken ct) =>
        allNames && request.Kind == ChangeFeedRequestKind.Pull ? Pull(caller, request.Token, ct, true) : Handle(caller, request, ct);

    private ChangeFeedResponse AddRoot(
        ChangeFeedCaller pipe,
        string? rootPath,
        CancellationToken cancellationToken)
    {
        var (caller, decision) = _admission.Evaluate(pipe, rootPath);

        if (decision.Status != ChangeFeedResponseStatus.Ok)
        {
            return ChangeFeedResponse.Failed(decision.Status, decision.Diagnostic);
        }

        var store = _storeFactory(caller.Value);
        using (store.EnterOwnerScope(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var roots = ExistingRoots(store);

            var admitted = new ChangeFeedSubscribedRoot(
                decision.CanonicalPath!,
                decision.Identity,
                CarryOrRenew(roots, decision.CanonicalPath!, decision.Identity));

            var replaced = roots
                .Where(root => !PathsMatch(root.RootPath, admitted.RootPath))
                .Append(admitted)
                .ToArray();

            if (replaced.Length > ChangeFeedSubscription.MaximumRoots)
            {
                return ChangeFeedResponse.Failed(
                    ChangeFeedResponseStatus.InvalidRequest,
                    $"Abonelik en çok {ChangeFeedSubscription.MaximumRoots} kök taşıyabilir.");
            }

            cancellationToken.ThrowIfCancellationRequested();
            store.WriteSubscription(new ChangeFeedSubscription(caller.Value, replaced));
            return ChangeFeedResponse.Ok(replaced.Select(root => root.RootPath).ToArray());
        }
    }

    private ChangeFeedResponse HoldLease(
        ChangeFeedCaller pipe,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);

        if (leaseSeconds <= 0 ||
            leaseSeconds > ChangeFeedWatcherLease.MaximumDuration.TotalSeconds)
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.InvalidRequest,
                "Kira süresi pozitif ve en çok " +
                $"{ChangeFeedWatcherLease.MaximumDuration.TotalSeconds:F0} saniye olmalıdır.");
        }

        var store = _storeFactory(caller.Value);
        using (store.EnterOwnerScope(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            store.HoldLease(TimeSpan.FromSeconds(leaseSeconds));
        }

        return ChangeFeedResponse.Ok();
    }

    private ChangeFeedResponse ReleaseLease(
        ChangeFeedCaller pipe,
        CancellationToken cancellationToken)
    {
        var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);

        var store = _storeFactory(caller.Value);
        using (store.EnterOwnerScope(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            store.ReleaseLease();
        }

        return ChangeFeedResponse.Ok();
    }

    private ChangeFeedResponse DrainAndHoldLease(
        ChangeFeedCaller pipe,
        int leaseSeconds,
        CancellationToken cancellationToken)
    {
        var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);

        if (leaseSeconds <= 0 ||
            leaseSeconds > ChangeFeedWatcherLease.MaximumDuration.TotalSeconds)
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.InvalidRequest,
                "Kira süresi pozitif ve en çok " +
                $"{ChangeFeedWatcherLease.MaximumDuration.TotalSeconds:F0} saniye olmalıdır.");
        }

        if (_handoffDrainer is null)
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.Unavailable,
                "Son boşaltma kullanılamıyor; watcher kirası alınmadı.");
        }

        var store = _storeFactory(caller.Value);
        using (store.EnterOwnerScope(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            store.ReleaseLease();

            using var budget = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken);
            budget.CancelAfter(_handoffDrainBudget);

            string? partial;
            try
            {
                partial = _handoffDrainer(caller.Value, budget.Token)
                    ? null
                    : budget.IsCancellationRequested
                        ? "Son boşaltma bütçesi aşıldı"
                        : "Son boşaltma tamamlanamadı";
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception failure)
            {
                partial = budget.IsCancellationRequested
                    ? "Son boşaltma bütçesi aşıldı"
                    : $"Son boşaltma hata verdi: {failure.Message}";
            }

            cancellationToken.ThrowIfCancellationRequested();
            store.HoldLease(TimeSpan.FromSeconds(leaseSeconds));

            return ChangeFeedResponse.Granted(
                partial is null
                    ? null
                    : partial + "; kira yine de verildi, devir eksik.");
        }
    }

    private static ChangeFeedRootGeneration CarryOrRenew(
        IReadOnlyList<ChangeFeedSubscribedRoot> roots,
        string canonicalPath,
        ChangeFeedRootIdentity identity)
    {
        var existing = roots.FirstOrDefault(root => PathsMatch(root.RootPath, canonicalPath));

        return existing is not null && existing.Identity == identity
            ? existing.Generation
            : ChangeFeedRootGeneration.New();
    }

    private ChangeFeedResponse RemoveRoot(
        ChangeFeedCaller pipe,
        string? rootPath,
        CancellationToken cancellationToken)
    {
        var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.InvalidRequest,
                "Kök yolu boş olamaz.");
        }

        var store = _storeFactory(caller.Value);
        using (store.EnterOwnerScope(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var remaining = ExistingRoots(store)
                .Where(root => !PathsMatch(root.RootPath, rootPath))
                .ToArray();

            cancellationToken.ThrowIfCancellationRequested();

            if (remaining.Length == 0)
            {
                store.DeleteSubscription();
            }
            else
            {
                store.WriteSubscription(new ChangeFeedSubscription(caller.Value, remaining));
            }

            return ChangeFeedResponse.Ok(remaining.Select(root => root.RootPath).ToArray());
        }
    }

    private ChangeFeedResponse ListRoots(
        ChangeFeedCaller pipe,
        CancellationToken cancellationToken)
    {
        var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);

        var store = _storeFactory(caller.Value);
        using (store.EnterOwnerScope(cancellationToken))
        {
            return ChangeFeedResponse.Ok(
                ExistingRoots(store).Select(root => root.RootPath).ToArray());
        }
    }

    private ChangeFeedResponse Pull(
        ChangeFeedCaller pipe,
        string? token,
        CancellationToken cancellationToken, bool allNames = false)
    {
        try
        {
            var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);
            var result = Session(pipe, caller, allNames).Pull(caller.Value, token, cancellationToken);

            return result.Status == ChangeFeedDeliveryStatus.Ok
                ? ChangeFeedResponse.Delivered(ChangeFeedDeliveryContract.ToWire(
                    result.Page!,
                    result.Continuation,
                    result.Receipt))
                : Refused(result.Status);
        }
        catch (ChangeFeedImpersonationException)
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.RootUnauthorized,
                "Çağıranın kimliği doğrulanamadı; teslim yapılmadı.");
        }
    }

    private ChangeFeedResponse Acknowledge(
        ChangeFeedCaller pipe,
        string? token,
        CancellationToken cancellationToken)
    {
        try
        {
            var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);
            var status = Session(pipe, caller)
                .Acknowledge(caller.Value, token, cancellationToken);

            return status == ChangeFeedDeliveryStatus.Ok
                ? ChangeFeedResponse.Ok()
                : Refused(status);
        }
        catch (ChangeFeedImpersonationException)
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.RootUnauthorized,
                "Çağıranın kimliği doğrulanamadı; hiçbir kayıt silinmedi.");
        }
    }

    private ChangeFeedPullSession Session(ChangeFeedCaller pipe, SecurityIdentifier caller, bool allNames = false) =>
        new(
            _storeFactory(caller.Value),
            _ledger,
            (subscription, slice, start, cancellationToken) =>
                ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid =>
                    sid.Equals(caller)
                        ? new ChangeFeedDeliveryProjector(
                            allNames ? root => new ChangeFeedPathAuthorizer(root, _ => true) : _authorizerFactory,
                            new ChangeFeedWireMeasure(),
                            _pageBudget).Walk(subscription, slice, start, cancellationToken)
                        : throw new ChangeFeedImpersonationException(
                            "Çağıranın kimliği istek ortasında değişti.")));

    private static ChangeFeedResponse Refused(ChangeFeedDeliveryStatus status) =>
        status == ChangeFeedDeliveryStatus.NoSubscription
            ? ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.NoSubscription,
                "Bu sahip için abonelik kaydı yok.")
            : ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.StaleChain,
                "Teslim zinciri geçersiz; baştan çekilmelidir.");

    private static IReadOnlyList<ChangeFeedSubscribedRoot> ExistingRoots(IChangeFeedStore store) =>
        store.ReadSubscription()?.Roots ?? Array.Empty<ChangeFeedSubscribedRoot>();

    private static bool PathsMatch(string left, string right)
    {
        try
        {
            return string.Equals(
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
                Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
                StringComparison.OrdinalIgnoreCase);
        }
        catch (Exception failure)
            when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }
    }

    public void Dispose() { _catalogs?.Dispose(); _inventory?.Dispose(); _volumes.Dispose(); }
}
