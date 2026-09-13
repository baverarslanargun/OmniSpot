using SmartFileLauncher.Core.ChangeFeed.Ipc;

namespace SmartFileLauncher.Core.Application.Indexing;

internal sealed record ContinuousPreparation(string[] ServiceRoots, string[] WatcherRoots, bool Available, string? Diagnostic);
internal sealed record ContinuousConsumption(bool Available, bool CaughtUp, int Events, string? Diagnostic = null);

public sealed partial class ChangeFeedIndexBridge
{
    internal async Task<ContinuousPreparation> PrepareContinuousAsync(IReadOnlyList<string> roots, bool fresh, CancellationToken ct)
    {
        var capability = await SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.GetCapabilities), ct).ConfigureAwait(false);
        if (capability is not { Status: ChangeFeedResponseStatus.Ok, Message: "continuous-usn-v1" })
            return new([], [], false, "Sürekli USN için güncel servis kullanılamıyor.");
        var supported = new List<string>(); var fallback = new List<string>(); IReadOnlyList<string>? subscriptions = null;
        foreach (var root in roots)
        {
            if (IsReparseRoot(root)) { fallback.Add(root); continue; }
            var response = await SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.PrepareContinuousRoot, root), ct).ConfigureAwait(false);
            if (response?.Status == ChangeFeedResponseStatus.UnsupportedFileSystem) fallback.Add(root);
            else if (response?.Status == ChangeFeedResponseStatus.Ok) { supported.Add(root); subscriptions = response.Roots; }
            else return new(supported.ToArray(), fallback.ToArray(), false, $"USN kökü hazır değil: {root}; {response?.Status}; {response?.Message}");
        }
        var listed = await SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.ListRoots), ct).ConfigureAwait(false);
        if (listed?.Status != ChangeFeedResponseStatus.Ok) return new(supported.ToArray(), fallback.ToArray(), false, "USN abonelikleri okunamadı.");
        subscriptions = listed.Roots ?? subscriptions;
        foreach (var obsolete in subscriptions?.Where(path => !supported.Contains(path, StringComparer.OrdinalIgnoreCase)) ?? [])
        {
            var response = await SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.RemoveRoot, obsolete), ct).ConfigureAwait(false);
            if (response?.Status != ChangeFeedResponseStatus.Ok) return new(supported.ToArray(), fallback.ToArray(), false, "Eski abonelik kaldırılamadı.");
        }
        if (supported.Count != 0)
        {
            if (!await HandOverAsync(ct).ConfigureAwait(false)) return new(supported.ToArray(), fallback.ToArray(), false, "Eski watcher kirası bırakılamadı.");
            if (fresh)
            {
                var drained = await DrainContinuousAsync(ct).ConfigureAwait(false);
                if (!drained) return new(supported.ToArray(), fallback.ToArray(), false, "İlk taramanın USN başlangıç sınırı henüz hazır değil.");
                var cleared = await ConsumeContinuousAsync(ct, discardBeforeFreshScan: true).ConfigureAwait(false);
                if (!cleared.Available || !cleared.CaughtUp)
                    return new(supported.ToArray(), fallback.ToArray(), false, cleared.Diagnostic ?? "Başlangıç teslimi tamamlanamadı.");
            }
        }
        return new(supported.ToArray(), fallback.ToArray(), true, null);
    }

    private static bool IsReparseRoot(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(current)))
        {
            try { if ((File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0) return true; }
            catch (Exception error) when (error is FileNotFoundException or DirectoryNotFoundException) { return false; }
        }
        return false;
    }

    internal async Task<bool> DrainContinuousAsync(CancellationToken ct)
    {
        var response = await SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.DrainContinuous), ct).ConfigureAwait(false);
        return response?.Status == ChangeFeedResponseStatus.Ok;
    }

    internal async Task<ContinuousConsumption> ConsumeContinuousAsync(CancellationToken ct, bool discardBeforeFreshScan = false)
    {
        var buffered = new List<ChangeFeedRootPageDto>(); string? continuation = null; var events = 0; var bufferedEvents = 0;
        for (var pageIndex = 0; pageIndex < MaximumPagesPerAdoption; pageIndex++)
        {
            var response = await SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.Pull, Token: continuation), ct).ConfigureAwait(false);
            if (response?.Status != ChangeFeedResponseStatus.Ok || response.Delivery is not { } page)
                return new(false, false, events, $"USN teslimi bekleniyor: {response?.Status}; {response?.Message}");
            buffered.AddRange(page.Roots); bufferedEvents += page.Roots.Sum(root => root.Events.Count);
            if (bufferedEvents > 1_000_000) throw new InvalidDataException("USN teslim zinciri sınırı aşıldı.");
            continuation = page.Continuation;
            if (continuation is not null) continue;
            if (page.Receipt is not null)
            {
                if (page.StableBatchId is not { Length: 64 } || page.CompletedThroughSequence <= 0)
                    return new(false, false, events, "USN tesliminin kalıcı kimliği eksik.");
                if (!discardBeforeFreshScan)
                    await _target.CommitContinuousAsync(buffered, page.StableBatchId, ct).ConfigureAwait(false);
                var ack = await SendAsync(new(ChangeFeedProtocol.Version, ChangeFeedRequestKind.Acknowledge, Token: page.Receipt), ct).ConfigureAwait(false);
                if (ack?.Status != ChangeFeedResponseStatus.Ok) return new(false, false, events, "USN teslimi kaydedildi; onayı yeniden denenecek.");
                events += bufferedEvents;
            }
            else if (bufferedEvents != 0 || buffered.Any(root => root.ProducerGap != ChangeFeed.ChangeFeedGapReason.None ||
                         root.ProducerFault != ChangeFeed.ChangeFeedFaultReason.None || root.AuthorizationGap || root.PayloadTooLarge))
                return new(false, false, events, "USN teslim makbuzu eksik.");
            buffered.Clear(); bufferedEvents = 0;
            if (!page.HasMore) return new(true, true, events);
        }
        return new(true, false, events, "USN kuyruğunun kalan kısmı sonraki çevrimde işlenecek.");
    }
}
