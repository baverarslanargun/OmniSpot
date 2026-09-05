using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedAdmissionService
{
    private readonly ChangeFeedRootAdmission _admission;
    private readonly Func<string, IChangeFeedStore> _storeFactory;

    public ChangeFeedAdmissionService(
        ChangeFeedRootAdmission admission,
        Func<string, IChangeFeedStore> storeFactory)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    }

    public ChangeFeedResponse Handle(
        NamedPipeServerStream pipe,
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

    private ChangeFeedResponse AddRoot(
        NamedPipeServerStream pipe,
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
        NamedPipeServerStream pipe,
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
        NamedPipeServerStream pipe,
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
}
