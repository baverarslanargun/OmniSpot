using System.Runtime.Versioning;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
internal sealed class SharedCatalogService(ChangeFeedAdmissionService admission,
    Func<string, ChangeFeedStoreLayout> layoutFactory, Func<string, IChangeFeedStore> storeFactory) : IDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, SharedCatalogSession> _sessions = new(StringComparer.Ordinal);
    private bool _disposed;

    internal ChangeFeedResponse Handle(ChangeFeedCaller caller, ChangeFeedRequest request)
    {
        var owner = caller.Run(sid => sid.Value);
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            _sessions.TryGetValue(owner, out var previous);
            if (request.Kind == ChangeFeedRequestKind.OpenCatalog)
            {
                var options = request.CatalogOptions;
                if (options?.RootsUtf16 is not { Count: > 0 and <= ChangeFeedSubscription.MaximumRoots } || !Enum.IsDefined(options.Method) ||
                    options.CacheKey is not { Length: > 0 and <= 128 } || options.BuildOptions is { IsValid: false })
                    return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "İndeks kapsamı veya yöntemi geçersiz.");
                string[] roots;
                try
                {
                    roots = options.RootsUtf16.Select(ChangeFeedDeliveryContract.DecodeScope)
                        .Select(path => Path.IsPathFullyQualified(path) ? Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)) : throw new ArgumentException())
                        .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                    roots = roots.Where(path => !roots.Any(other => other != path && UsnRootScope.TryCanonicalize(other, path, out _))).ToArray();
                }
                catch (Exception failure) when (failure is FormatException or ArgumentException or InvalidDataException)
                { return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "İndeks konumu çözülemedi."); }
                var allowed = caller.Run(_ => roots.All(CanList));
                if (!allowed) return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.RootUnauthorized, "Seçilen klasörlerden biri okunamıyor.");
                var layout = layoutFactory(owner);
                if (roots.Any(root => UsnRootScope.TryCanonicalize(layout.StoreRoot, root, out _)))
                    return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.RootUnauthorized, "Servisin katalog deposu indeks kapsamı olamaz.");
                var captured = caller.Capture();
                try
                {
                    var next = new SharedCatalogSession(admission, captured, layout, storeFactory(owner), roots, options.Method,
                        options.Rebuild, previous, options.CacheKey, IndexBuildOptions.Normalize(options.BuildOptions));
                    previous?.Stop();
                    _sessions[owner] = next;
                    next.Start();
                    return Response(next.Status);
                }
                catch { captured.Dispose(); throw; }
            }
            if (previous is null || previous.Status.Session != request.Token)
                return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.StaleChain, "Katalog oturumu yeniden açılmalı.");
            if (request.Kind == ChangeFeedRequestKind.CloseCatalog)
            {
                previous.Stop();
                return Response(previous.Status);
            }
            previous.Touch();
            if (request.Kind is ChangeFeedRequestKind.RecordCatalogOpen or ChangeFeedRequestKind.RepairCatalog or ChangeFeedRequestKind.SynchronizeCatalog)
            {
                string path;
                try { path = ChangeFeedDeliveryContract.DecodeScope(request.RootPath ?? ""); }
                catch (Exception failure) when (failure is FormatException or InvalidDataException)
                { return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "Dosya yolu geçersiz."); }
                var operation = previous.EnqueueRequest(path, request.Kind == ChangeFeedRequestKind.RecordCatalogOpen,
                    request.Kind == ChangeFeedRequestKind.SynchronizeCatalog);
                if (operation == 0)
                    return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.InvalidRequest, "Katalog isteği kabul edilemedi.");
                return Response(previous.Status with { AcceptedOperation = operation });
            }
            return Response(previous.Status);
        }
    }

    private static ChangeFeedResponse Response(SharedCatalogStatus status) =>
        new(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok, Catalog: status);

    internal static bool CanList(string path)
    {
        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(path).GetEnumerator();
            entries.MoveNext();
            return true;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException) { return false; }
    }

    public void Dispose()
    {
        SharedCatalogSession[] sessions;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            sessions = _sessions.Values.ToArray();
            foreach (var session in sessions) session.Stop();
            _sessions.Clear();
        }
        Task.WhenAll(sessions.Select(session => session.Completion)).GetAwaiter().GetResult();
    }
}
