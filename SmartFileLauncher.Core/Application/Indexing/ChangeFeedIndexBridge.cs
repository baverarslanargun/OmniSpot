using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Application.Indexing;

public interface IChangeFeedRequestChannel
{
    Task<ChangeFeedResponse> SendAsync(
        ChangeFeedRequest request,
        CancellationToken cancellationToken);
}

public interface IChangeFeedIndexTarget
{
    Task CommitContinuousAsync(IReadOnlyList<ChangeFeedRootPageDto> roots, string deliveryId,
        CancellationToken cancellationToken) => throw new NotSupportedException("Kalıcı teslim hedefi yok.");
    bool Apply(IReadOnlyList<FileChangeEvent> changes);

    Task<IReadOnlyList<string>> ApplyOrRepairAsync(IReadOnlyList<FileChangeEvent> changes, bool withinLifecycle,
        CancellationToken cancellationToken) => Task.FromResult<IReadOnlyList<string>>(Apply(changes) ? [] :
            changes.SelectMany(change => change.OldPath is null ? new[] { change.FullPath } : new[] { change.FullPath, change.OldPath }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray());

    bool DeferRepairs(IReadOnlyList<string> scopes) => false;

    Task<bool> ResynchronizeAsync(
        string rootPath,
        bool withinLifecycle,
        CancellationToken cancellationToken);
}

public enum ChangeFeedAdoptionStatus
{
    Adopted,
    NothingToAdopt,
    Incomplete,
    ServiceUnavailable,
    Refused
}

public sealed record ChangeFeedAdoptionResult(
    ChangeFeedAdoptionStatus Status,
    int RootsSubscribed,
    int PagesApplied,
    int EventsApplied,
    int RootsResynchronized,
    bool LeaseHeld,
    string? Diagnostics = null,
    IReadOnlyList<string>? PendingRepairScopes = null,
    bool OnlyKnownRepairs = false);

public sealed partial class ChangeFeedIndexBridge
{
    public const int MaximumPagesPerAdoption = 512;

    private readonly IChangeFeedRequestChannel _channel;
    private readonly IChangeFeedIndexTarget _target;
    private readonly TimeSpan _leaseDuration;

    public ChangeFeedIndexBridge(
        IChangeFeedRequestChannel channel,
        IChangeFeedIndexTarget target,
        TimeSpan? leaseDuration = null)
    {
        _channel = channel ?? throw new ArgumentNullException(nameof(channel));
        _target = target ?? throw new ArgumentNullException(nameof(target));
        _leaseDuration = leaseDuration ?? DefaultLeaseDuration;

        if (_leaseDuration <= TimeSpan.Zero ||
            _leaseDuration > ChangeFeedWatcherLease.MaximumDuration)
        {
            throw new ArgumentOutOfRangeException(nameof(leaseDuration));
        }
    }

    public static TimeSpan DefaultLeaseDuration => TimeSpan.FromMinutes(2);

    public static TimeSpan DefaultRenewInterval => TimeSpan.FromSeconds(40);

    public async Task<ChangeFeedAdoptionResult> AdoptAsync(
        IReadOnlyList<string> roots,
        CancellationToken cancellationToken,
        bool withinLifecycle = false,
        Func<CancellationToken, Task<bool>>? validateInitialInventory = null)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var subscribed = 0;
        string? diagnostics = null;
        IReadOnlyList<string>? subscriptions = null;

        foreach (var root in roots)
        {
            cancellationToken.ThrowIfCancellationRequested();

            var response = await SendAsync(
                new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.AddRoot,
                    root),
                cancellationToken).ConfigureAwait(false);

            if (response is null)
            {
                return Unavailable(subscribed);
            }

            if (response.Status == ChangeFeedResponseStatus.Ok)
            {
                subscribed++;
                subscriptions = response.Roots ?? subscriptions;
                continue;
            }

            diagnostics ??= $"{root}: {response.Status} {response.Message}";
        }

        if (subscribed == 0)
        {
            return new ChangeFeedAdoptionResult(
                ChangeFeedAdoptionStatus.Refused,
                0,
                0,
                0,
                0,
                false,
                diagnostics ?? "Hiçbir kök abone edilemedi.");
        }

        if (subscribed == roots.Count && subscriptions is not null)
        {
            var desired = roots.Select(CanonicalRoot).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var obsolete in subscriptions.Where(root => !desired.Contains(CanonicalRoot(root))))
            {
                var removed = await SendAsync(new ChangeFeedRequest(ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.RemoveRoot, obsolete), cancellationToken).ConfigureAwait(false);
                if (removed?.Status != ChangeFeedResponseStatus.Ok)
                    return new ChangeFeedAdoptionResult(ChangeFeedAdoptionStatus.Incomplete,
                        subscribed, 0, 0, 0, false, "Önceki indeks kapsamının aboneliği kaldırılamadı.");
            }
        }

        var lease = await SendAsync(
                new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.DrainAndHoldLease,
                    LeaseSeconds: (int)_leaseDuration.TotalSeconds),
            cancellationToken).ConfigureAwait(false);

        if (lease is null)
        {
            return Unavailable(subscribed);
        }

        if (lease.Status != ChangeFeedResponseStatus.Ok)
        {
            return new ChangeFeedAdoptionResult(
                ChangeFeedAdoptionStatus.Incomplete,
                subscribed,
                0,
                0,
                0,
                false,
                Reason($"Kira alınamadı: {lease.Status} {lease.Message}", diagnostics));
        }

        var partialHandOver = !string.IsNullOrWhiteSpace(lease.Message);
        if (partialHandOver)
        {
            diagnostics = Reason(lease.Message!, diagnostics);
        }

        Consumption? consumed;
        try
        {
            consumed = await ConsumeWithLeaseAsync(roots, withinLifecycle, cancellationToken,
                validateInitialInventory).ConfigureAwait(false);
        }
        catch
        {
            await HandOverAsync(CancellationToken.None).ConfigureAwait(false);
            throw;
        }

        if (consumed is null)
        {
            await HandOverAsync(CancellationToken.None).ConfigureAwait(false);
            return Unavailable(subscribed);
        }

        if (!consumed.Complete)
        {
            await HandOverAsync(CancellationToken.None).ConfigureAwait(false);

            return new ChangeFeedAdoptionResult(
                ChangeFeedAdoptionStatus.Incomplete,
                subscribed,
                consumed.Pages,
                consumed.Events,
                consumed.Resynchronized,
                false,
                Reason(
                    "Kuyruk boşaltılamadı; kira geri verildi.",
                    consumed.Diagnostics ?? diagnostics), consumed.PendingRepairScopes,
                !partialHandOver && consumed.OnlyKnownRepairs);
        }

        return new ChangeFeedAdoptionResult(
            partialHandOver
                ? ChangeFeedAdoptionStatus.Incomplete
                : consumed.Pages == 0 && consumed.Events == 0 && consumed.Resynchronized == 0
                    ? ChangeFeedAdoptionStatus.NothingToAdopt
                    : ChangeFeedAdoptionStatus.Adopted,
            subscribed,
            consumed.Pages,
            consumed.Events,
            consumed.Resynchronized,
            true,
            diagnostics ?? consumed.Diagnostics, consumed.PendingRepairScopes,
            !partialHandOver && consumed.PendingRepairScopes is { Count: > 0 });
    }

    public async Task<bool> RenewLeaseAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            new ChangeFeedRequest(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.HoldLease,
                LeaseSeconds: (int)_leaseDuration.TotalSeconds),
            cancellationToken).ConfigureAwait(false);

        return response is not null && response.Status == ChangeFeedResponseStatus.Ok;
    }

    public async Task<bool> HandOverAsync(CancellationToken cancellationToken)
    {
        var response = await SendAsync(
            new ChangeFeedRequest(
                ChangeFeedProtocol.Version,
                ChangeFeedRequestKind.ReleaseLease),
            cancellationToken).ConfigureAwait(false);

        return response is not null && response.Status == ChangeFeedResponseStatus.Ok;
    }

    private async Task<Consumption?> ConsumeWithLeaseAsync(IReadOnlyList<string> roots, bool withinLifecycle,
        CancellationToken ct, Func<CancellationToken, Task<bool>>? validateInitialInventory)
    {
        using var lifetime = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var renewalFailed = 0;
        var renewal = KeepLeaseAsync();
        Consumption? consumed = null;
        try
        {
            var baseline = validateInitialInventory is not null &&
                await validateInitialInventory(lifetime.Token).ConfigureAwait(false)
                    ? roots.ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
            consumed = await ConsumeAsync(withinLifecycle, lifetime.Token, baseline,
                validateInitialInventory).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested && Volatile.Read(ref renewalFailed) != 0) { }
        finally
        {
            lifetime.Cancel();
            await renewal.ConfigureAwait(false);
        }
        return Volatile.Read(ref renewalFailed) == 0 ? consumed : Incomplete(
            consumed?.Pages ?? 0, consumed?.Events ?? 0, consumed?.Resynchronized ?? 0,
            "Devir sırasında takip kirası yenilenemedi; tüketim durduruldu.");

        async Task KeepLeaseAsync()
        {
            using var timer = new PeriodicTimer(TimeSpan.FromTicks(Math.Max(1, _leaseDuration.Ticks / 3)));
            try
            {
                while (await timer.WaitForNextTickAsync(lifetime.Token).ConfigureAwait(false))
                {
                    if (await RenewLeaseAsync(lifetime.Token).ConfigureAwait(false)) continue;
                    Interlocked.Exchange(ref renewalFailed, 1);
                    lifetime.Cancel();
                    break;
                }
            }
            catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        }
    }

    private async Task<Consumption?> ConsumeAsync(
        bool withinLifecycle,
        CancellationToken cancellationToken,
        HashSet<string>? baselineRoots = null,
        Func<CancellationToken, Task<bool>>? validateInitialInventory = null)
    {
        var pages = 0;
        var fetches = 0;
        var events = 0;
        var resynchronized = 0;
        string? diagnostics = null;
        string? blocker = null;
        string? continuation = null;
        var restarted = false;
        var deferred = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        while (fetches < MaximumPagesPerAdoption)
        {
            cancellationToken.ThrowIfCancellationRequested();
            fetches++;

            var response = await SendAsync(
                new ChangeFeedRequest(
                    ChangeFeedProtocol.Version,
                    ChangeFeedRequestKind.Pull,
                    Token: continuation),
                cancellationToken).ConfigureAwait(false);

            if (response is null)
            {
                return null;
            }

            if (response.Status == ChangeFeedResponseStatus.StaleChain)
            {
                if (restarted)
                {
                    return Incomplete(
                        pages,
                        events,
                        resynchronized,
                        Reason("Zincir iki kez düştü; teslim bırakıldı.", diagnostics));
                }

                restarted = true;
                continuation = null;
                continue;
            }

            if (response.Status == ChangeFeedResponseStatus.NoSubscription)
            {
                return Incomplete(
                    pages,
                    events,
                    resynchronized,
                    Reason("Abonelik yok; teslim alınamadı.", diagnostics));
            }

            if (response.Status != ChangeFeedResponseStatus.Ok ||
                response.Delivery is not { } delivery)
            {
                return Incomplete(
                    pages,
                    events,
                    resynchronized,
                    Reason($"{response.Status} {response.Message}", diagnostics));
            }

            var landed = true;
            var knownDelivery = delivery.Roots.All(root => !NeedsResynchronization(root) || TryGetAuthorizationScopes(root, out _));
            foreach (var root in delivery.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

                if (baselineRoots?.Contains(root.RootPath) == true) continue;

                if (TryGetAuthorizationScopes(root, out var scopes))
                {
                    var pending = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                    var changes = root.Events.Select(ToFileChange).ToArray();
                    if (changes.Length > 0)
                        pending.UnionWith(await ApplyKnownChangesAsync(changes, withinLifecycle, cancellationToken).ConfigureAwait(false));
                    foreach (var scope in scopes)
                    {
                        if (await RepairKnownScopeAsync(scope, withinLifecycle, cancellationToken).ConfigureAwait(false))
                            resynchronized++;
                        else pending.Add(scope);
                    }
                    if (pending.Count > 0 && !DeferKnownRepairs(pending.ToArray()))
                        return Incomplete(pages, events, resynchronized,
                            $"{root.RootPath}: yerel onarımlar kaydedilemedi; sayfa onaylanmadı.", pending.ToArray(), onlyKnownRepairs: landed && knownDelivery);
                    deferred.UnionWith(pending);
                    events += changes.Length;
                    diagnostics ??= $"{root.RootPath}: {scopes.Count} alt kapsam denetlendi.";
                    continue;
                }

                if (NeedsResynchronization(root))
                {
                    diagnostics ??= DescribeGap(root);

                    if (await _target
                            .ResynchronizeAsync(root.RootPath, withinLifecycle, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        resynchronized++;
                    }
                    else
                    {
                        landed = false;
                        blocker ??= $"{root.RootPath}: uzlaştırma boşluğu kapatamadı; " +
                            DescribeGap(root);
                    }

                    continue;
                }

                if (root.Events.Count == 0)
                {
                    continue;
                }

                var visibleChanges = root.Events.Select(ToFileChange).ToArray();
                var failedScopes = await ApplyKnownChangesAsync(visibleChanges, withinLifecycle, cancellationToken).ConfigureAwait(false);
                if (failedScopes.Count == 0)
                {
                    events += root.Events.Count;
                }
                else
                {
                    var pending = new HashSet<string>(failedScopes, StringComparer.OrdinalIgnoreCase);
                    if (!DeferKnownRepairs(pending.ToArray()))
                        return Incomplete(pages, events, resynchronized,
                            $"{root.RootPath}: yerel onarımlar kaydedilemedi; sayfa onaylanmadı.", pending.ToArray(), onlyKnownRepairs: landed && knownDelivery);
                    deferred.UnionWith(pending);
                    events += visibleChanges.Length;
                    diagnostics ??= $"{root.RootPath}: yerel onarımlar kaydedildi.";
                }
            }

            if (delivery.Roots.Count > 0)
            {
                pages++;
            }

            if (!landed)
            {
                return Incomplete(
                    pages,
                    events,
                    resynchronized,
                    Reason(
                        blocker ?? "Sayfa tam uygulanamadı; onay gönderilmedi.",
                        diagnostics));
            }

            if (baselineRoots is not null &&
                !await validateInitialInventory!(cancellationToken).ConfigureAwait(false))
                return Incomplete(pages, events, resynchronized,
                    "İlk envanter veya watcher yakalaması geçerliliğini kaybetti; kuyruk onaylanmadı.");

            if (delivery.Receipt is { } receipt)
            {
                var acknowledged = await SendAsync(
                    new ChangeFeedRequest(
                        ChangeFeedProtocol.Version,
                        ChangeFeedRequestKind.Acknowledge,
                        Token: receipt),
                    cancellationToken).ConfigureAwait(false);

                if (acknowledged is null)
                {
                    return null;
                }

                if (acknowledged.Status != ChangeFeedResponseStatus.Ok)
                {
                    return Incomplete(
                        pages,
                        events,
                        resynchronized,
                        Reason($"Onay reddedildi: {acknowledged.Status}", diagnostics));
                }
                fetches = 0;
                restarted = false;
            }

            continuation = delivery.Continuation;
            if (continuation is null)
            {
                if (delivery.HasMore)
                {
                    if (delivery.Receipt is null)
                        return Incomplete(pages, events, resynchronized, "Devamı olan sayfa ne devam anahtarı ne onay makbuzu içeriyor.");
                    continue;
                }
                return new Consumption(pages, events, resynchronized, true, diagnostics, deferred.ToArray());
            }
        }

        return Incomplete(
            pages,
            events,
            resynchronized,
            Reason($"Sayfa üst sınırına ulaşıldı ({MaximumPagesPerAdoption}).", diagnostics));
    }

    private static Consumption Incomplete(
        int pages,
        int events,
        int resynchronized,
        string diagnostics,
        IReadOnlyList<string>? pendingRepairScopes = null, bool onlyKnownRepairs = false) =>
        new(pages, events, resynchronized, false, diagnostics, pendingRepairScopes, onlyKnownRepairs);

    private static void AddChangedPaths(IEnumerable<FileChangeEvent> changes, HashSet<string> paths)
    {
        foreach (var change in changes)
        {
            paths.Add(change.FullPath);
            if (change.OldPath is not null) paths.Add(change.OldPath);
        }
    }

    private async Task<IReadOnlyList<string>> ApplyKnownChangesAsync(IReadOnlyList<FileChangeEvent> changes,
        bool withinLifecycle, CancellationToken ct)
    {
        try { return await _target.ApplyOrRepairAsync(changes, withinLifecycle, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception)
        {
            var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            AddChangedPaths(changes, paths);
            return paths.ToArray();
        }
    }

    private async Task<bool> RepairKnownScopeAsync(string scope, bool withinLifecycle, CancellationToken ct)
    {
        try { return await _target.ResynchronizeAsync(scope, withinLifecycle, ct).ConfigureAwait(false); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception) { return false; }
    }

    private bool DeferKnownRepairs(IReadOnlyList<string> scopes)
    {
        try { return _target.DeferRepairs(scopes); }
        catch (Exception) { return false; }
    }

    private async Task<ChangeFeedResponse?> SendAsync(
        ChangeFeedRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return await _channel.SendAsync(request, cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            return null;
        }
    }

    private static string Reason(string primary, string? context) =>
        string.IsNullOrWhiteSpace(context) ? primary : $"{primary} | {context}";

    private static string CanonicalRoot(string path) => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));

    private static bool TryGetAuthorizationScopes(ChangeFeedRootPageDto root, out IReadOnlyList<string> scopes)
    {
        scopes = [];
        if ((!root.AuthorizationGap && !root.PayloadTooLarge) || root.ProducerGap != ChangeFeedGapReason.None ||
            root.ProducerFault != ChangeFeedFaultReason.None ||
            root.AuthorizationScopesUtf16 is not { Count: > 0 and <= 32 } encoded) return false;
        try
        {
            var rootPath = CanonicalRoot(root.RootPath);
            var prefix = Path.EndsInDirectorySeparator(rootPath) ? rootPath : rootPath + Path.DirectorySeparatorChar;
            var decoded = new List<string>();
            foreach (var value in encoded)
            {
                var path = ChangeFeedDeliveryContract.DecodeScope(value);
                if (!Path.IsPathFullyQualified(path)) return false;
                path = CanonicalRoot(path);
                if (!string.Equals(path, rootPath, StringComparison.OrdinalIgnoreCase) &&
                    !path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return false;
                if (!decoded.Contains(path, StringComparer.OrdinalIgnoreCase)) decoded.Add(path);
            }
            scopes = decoded.Where(path => !decoded.Any(parent => parent.Length < path.Length &&
                path.StartsWith(Path.EndsInDirectorySeparator(parent) ? parent : parent + Path.DirectorySeparatorChar,
                    StringComparison.OrdinalIgnoreCase))).ToArray();
            return scopes.Count > 0;
        }
        catch (Exception error) when (error is ArgumentException or NotSupportedException or
            PathTooLongException or FormatException or InvalidDataException) { return false; }
    }

    private static bool NeedsResynchronization(ChangeFeedRootPageDto root) =>
        root.ProducerGap != ChangeFeedGapReason.None ||
        root.ProducerFault != ChangeFeedFaultReason.None ||
        root.AuthorizationGap ||
        root.PayloadTooLarge;

    private static string DescribeGap(ChangeFeedRootPageDto root) =>
        $"{root.RootPath}: boşluk={root.ProducerGap} arıza={root.ProducerFault} " +
        $"yetki={root.AuthorizationGap} taşma={root.PayloadTooLarge}";

    private static FileChangeEvent ToFileChange(ChangeFeedEventDto dto) =>
        new()
        {
            ChangeType = dto.Kind switch
            {
                ChangeFeedEventKind.Created => FileChangeType.Created,
                ChangeFeedEventKind.Deleted => FileChangeType.Deleted,
                ChangeFeedEventKind.Renamed => FileChangeType.Renamed,
                _ => FileChangeType.Modified
            },
            FullPath = dto.Path,
            OldPath = dto.OldPath,
            IsDirectory = dto.IsDirectory,
            Timestamp = DateTime.UtcNow
        };

    private static ChangeFeedAdoptionResult Unavailable(int subscribed) =>
        new(
            ChangeFeedAdoptionStatus.ServiceUnavailable,
            subscribed,
            0,
            0,
            0,
            false,
            "Değişiklik akışı servisine ulaşılamadı.");

    private sealed record Consumption(
        int Pages,
        int Events,
        int Resynchronized,
        bool Complete,
        string? Diagnostics,
        IReadOnlyList<string>? PendingRepairScopes = null,
        bool OnlyKnownRepairs = false);
}
