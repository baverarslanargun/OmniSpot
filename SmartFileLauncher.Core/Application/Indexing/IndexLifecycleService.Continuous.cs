namespace SmartFileLauncher.Core.Application.Indexing;

public sealed partial class IndexLifecycleService
{
    private async Task RunContinuousAsync(IReadOnlyList<string> roots, CancellationToken ct)
    {
        using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1));
        var prepared = _indexManager.LiveCoverageReady;
        string? lastError = null;
        try
        {
            while (await timer.WaitForNextTickAsync(ct).ConfigureAwait(false))
            {
                try
                {
                    if (_changeFeed is not null)
                    {
                        if (!prepared)
                        {
                            var result = await _changeFeed.PrepareContinuousAsync(roots, false, ct).ConfigureAwait(false);
                            prepared = result.Available;
                            if (!prepared) throw new IOException(result.Diagnostic);
                            _indexManager.UpdateLiveCoverage(result.ServiceRoots, result.WatcherRoots);
                        }
                        if (_indexManager.LiveServiceRoots.Length > 0)
                        {
                            var drained = await _changeFeed.DrainContinuousAsync(ct).ConfigureAwait(false);
                            var consumed = await _changeFeed.ConsumeContinuousAsync(ct).ConfigureAwait(false);
                            if (!consumed.Available) { prepared = false; throw new IOException(consumed.Diagnostic); }
                            if (drained && consumed.CaughtUp) _indexManager.CompleteLiveBootstrap();
                            if (_indexManager.LiveNeedsResubscribe) { prepared = false; continue; }
                            if (!drained) throw new IOException("USN günlüğünün güncel sınırına henüz ulaşılamadı; tekrar denenecek.");
                        }
                    }
                    await _indexManager.RetryLiveRepairsAsync(ct).ConfigureAwait(false);
                    if (lastError is not null) Notice?.Invoke("USN değişiklik akışı yeniden güncel.");
                    lastError = null;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                catch (Exception error)
                {
                    if (lastError != error.Message) Notice?.Invoke("Katalog açık; değişiklik teslimi bekleniyor: " + error.Message);
                    lastError = error.Message;
                }
            }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { }
    }
}
