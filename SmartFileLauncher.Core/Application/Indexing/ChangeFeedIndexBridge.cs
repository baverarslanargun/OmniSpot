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
    bool Apply(IReadOnlyList<FileChangeEvent> changes);

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
    string? Diagnostics = null);

public sealed class ChangeFeedIndexBridge
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
        bool withinLifecycle = false)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var subscribed = 0;
        string? diagnostics = null;

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

        Consumption? consumed;
        try
        {
            consumed = await ConsumeAsync(withinLifecycle, cancellationToken).ConfigureAwait(false);
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
                    consumed.Diagnostics ?? diagnostics));
        }

        return new ChangeFeedAdoptionResult(
            consumed.Pages == 0 && consumed.Events == 0 && consumed.Resynchronized == 0
                ? ChangeFeedAdoptionStatus.NothingToAdopt
                : ChangeFeedAdoptionStatus.Adopted,
            subscribed,
            consumed.Pages,
            consumed.Events,
            consumed.Resynchronized,
            true,
            diagnostics ?? consumed.Diagnostics);
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

    private async Task<Consumption?> ConsumeAsync(
        bool withinLifecycle,
        CancellationToken cancellationToken)
    {
        var pages = 0;
        var fetches = 0;
        var events = 0;
        var resynchronized = 0;
        string? diagnostics = null;
        string? blocker = null;
        string? continuation = null;
        var restarted = false;

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
            foreach (var root in delivery.Roots)
            {
                cancellationToken.ThrowIfCancellationRequested();

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

                if (_target.Apply(root.Events.Select(ToFileChange).ToArray()))
                {
                    events += root.Events.Count;
                }
                else
                {
                    diagnostics ??= $"{root.RootPath}: olaylar tam uygulanamadı; kök uzlaştırıldı.";
                    if (await _target
                            .ResynchronizeAsync(root.RootPath, withinLifecycle, cancellationToken)
                            .ConfigureAwait(false))
                    {
                        resynchronized++;
                    }
                    else
                    {
                        landed = false;
                        blocker ??= $"{root.RootPath}: olaylar uygulanamadı ve " +
                            "uzlaştırma da başarısız oldu.";
                    }
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
            }

            continuation = delivery.Continuation;
            if (continuation is null)
            {
                return new Consumption(pages, events, resynchronized, true, diagnostics);
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
        string diagnostics) =>
        new(pages, events, resynchronized, false, diagnostics);

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
        string? Diagnostics);
}
