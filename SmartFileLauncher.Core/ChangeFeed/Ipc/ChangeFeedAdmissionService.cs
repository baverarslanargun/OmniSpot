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
    private readonly object _gate = new();

    public ChangeFeedAdmissionService(
        ChangeFeedRootAdmission admission,
        Func<string, IChangeFeedStore> storeFactory)
    {
        _admission = admission ?? throw new ArgumentNullException(nameof(admission));
        _storeFactory = storeFactory ?? throw new ArgumentNullException(nameof(storeFactory));
    }

    public ChangeFeedResponse Handle(NamedPipeServerStream pipe, ChangeFeedRequest request)
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
                ChangeFeedRequestKind.AddRoot => AddRoot(pipe, request.RootPath),
                ChangeFeedRequestKind.RemoveRoot => RemoveRoot(pipe, request.RootPath),
                ChangeFeedRequestKind.ListRoots => ListRoots(pipe),
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

    private ChangeFeedResponse AddRoot(NamedPipeServerStream pipe, string? rootPath)
    {
        var (caller, decision) = _admission.Evaluate(pipe, rootPath);

        if (decision.Status != ChangeFeedResponseStatus.Ok)
        {
            return ChangeFeedResponse.Failed(decision.Status, decision.Diagnostic);
        }

        lock (_gate)
        {
            var store = _storeFactory(caller.Value);
            var roots = ExistingRoots(store);

            var admitted = new ChangeFeedSubscribedRoot(decision.CanonicalPath!, decision.Identity);
            var replaced = roots
                .Where(root => !PathsMatch(root.RootPath, admitted.RootPath))
                .Append(admitted)
                .ToArray();

            store.WriteSubscription(new ChangeFeedSubscription(caller.Value, replaced));
            return ChangeFeedResponse.Ok(replaced.Select(root => root.RootPath).ToArray());
        }
    }

    private ChangeFeedResponse RemoveRoot(NamedPipeServerStream pipe, string? rootPath)
    {
        var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);

        if (string.IsNullOrWhiteSpace(rootPath))
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.InvalidRequest,
                "Kök yolu boş olamaz.");
        }

        lock (_gate)
        {
            var store = _storeFactory(caller.Value);
            var remaining = ExistingRoots(store)
                .Where(root => !PathsMatch(root.RootPath, rootPath))
                .ToArray();

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

    private ChangeFeedResponse ListRoots(NamedPipeServerStream pipe)
    {
        var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);

        lock (_gate)
        {
            var store = _storeFactory(caller.Value);
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
