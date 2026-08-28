using System.IO;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Diagnostics;

namespace SmartFileLauncher.UI.Services;

public sealed class DiagnosticsSession : IDisposable
{
    private static readonly TimeSpan DiskCacheSampleInterval = TimeSpan.FromSeconds(60);

    private readonly ApplicationLog _applicationLog;
    private readonly IIndexLifecycleService _indexLifecycle;
    private readonly IThumbnailService _thumbnails;
    private readonly Func<string, string>? _sanitizeDiagnosticValue;
    private readonly DispatcherTimer _sampleTimer = new();
    private readonly DispatcherTimer _diskCacheTimer = new();
    private readonly CancellationTokenSource _diskCacheCancellation = new();

    private bool _liveViewActive;
    private bool _disposed;

    public DiagnosticsSession(
        ApplicationLog applicationLog,
        DiagnosticsCollector collector,
        IIndexLifecycleService indexLifecycle,
        IThumbnailService thumbnails,
        int sampleIntervalSeconds,
        Func<string, string>? sanitizeDiagnosticValue = null)
    {
        _applicationLog = applicationLog ?? throw new ArgumentNullException(nameof(applicationLog));
        _indexLifecycle = indexLifecycle ?? throw new ArgumentNullException(nameof(indexLifecycle));
        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _sanitizeDiagnosticValue = sanitizeDiagnosticValue;
        Collector = collector ?? throw new ArgumentNullException(nameof(collector));
        MetricLog = new DiagnosticsMetricLog(sanitizeValue: _sanitizeDiagnosticValue);

        _applicationLog.MessageWritten += FileLog.Write;
        _indexLifecycle.ReconciliationStateChanged += HandleReconciliationStateChanged;

        _sampleTimer.Interval = TimeSpan.FromSeconds(
            Math.Clamp(sampleIntervalSeconds, 1, 3600));
        _sampleTimer.Tick += (_, __) => WriteSample();

        _diskCacheTimer.Interval = DiskCacheSampleInterval;
        _diskCacheTimer.Tick += (_, __) => RefreshDiskCacheStats();
    }

    public DiagnosticsCollector Collector { get; }

    public DiagnosticsFileLog FileLog { get; } = new();

    public DiagnosticsMetricLog MetricLog { get; }

    public bool StartFileLogging(
        string directory,
        IReadOnlyList<KeyValuePair<string, string>> stamps)
    {
        return FileLog.Start(directory, stamps);
    }

    public void StopFileLogging()
    {
        FileLog.Stop();
    }

    public bool StartMetricLogging(string directory)
    {
        if (!MetricLog.Start(directory))
        {
            _sampleTimer.Stop();
            UpdateDiskCacheTimer();
            return false;
        }

        WriteSample();
        _sampleTimer.Start();
        UpdateDiskCacheTimer();
        return true;
    }

    public void StopMetricLogging()
    {
        _sampleTimer.Stop();
        MetricLog.Stop();
        UpdateDiskCacheTimer();
    }

    public void SetLiveViewActive(bool active)
    {
        _liveViewActive = active;
        UpdateDiskCacheTimer();
    }

    public void RecordFolder(string folderPath, int itemCount, bool truncated)
    {
        Collector.RecordFolder(folderPath, itemCount, truncated);

        if (!MetricLog.IsWriting) return;

        Collector.Refresh();
        MetricLog.WriteEvent("klasör açıldı", Path.GetFileName(folderPath), itemCount);
        MetricLog.WriteSample(Collector.Metrics.Snapshot());
    }

    public void RecordSearch(int queryLength, TimeSpan duration, int resultCount)
    {
        Collector.RecordSearch(queryLength, duration, resultCount);

        if (!MetricLog.IsWriting) return;

        Collector.Refresh();
        MetricLog.WriteEvent(
            "arama",
            $"{queryLength} karakter · {resultCount} sonuç",
            duration.TotalMilliseconds);
        MetricLog.WriteSample(Collector.Metrics.Snapshot());
    }

    public void RecordEvent(
        string name,
        string? detail = null,
        double? numericValue = null)
    {
        if (!MetricLog.IsWriting) return;

        Collector.Refresh();
        MetricLog.WriteEvent(name, detail ?? string.Empty, numericValue);
        MetricLog.WriteSample(Collector.Metrics.Snapshot());
    }

    private void HandleReconciliationStateChanged(bool isRunning)
    {
        if (!MetricLog.IsWriting) return;

        Collector.Refresh();

        if (isRunning)
        {
            MetricLog.WriteEvent("uzlaştırma başladı", string.Empty, null);
        }
        else
        {
            var report = _indexLifecycle.GetDiagnosticsReport();
            var republish = report.RepublishedDuringLastReconciliation
                ? DiagnosticsCollector.FormatDuration(report.LastRepublishDuration)
                : "yok";
            MetricLog.WriteEvent(
                "uzlaştırma bitti",
                $"{report.LastReconciliationChanges} değişiklik · yeniden yayım {republish}",
                report.LastReconciliationChanges);
        }

        MetricLog.WriteSample(Collector.Metrics.Snapshot());
    }

    private void UpdateDiskCacheTimer()
    {
        if (MetricLog.IsWriting || _liveViewActive)
        {
            if (!_diskCacheTimer.IsEnabled)
            {
                _diskCacheTimer.Start();
                RefreshDiskCacheStats();
            }

            return;
        }

        _diskCacheTimer.Stop();
    }

    private void RefreshDiskCacheStats()
    {
        if (_disposed) return;
        _ = RefreshDiskCacheStatsAsync();
    }

    private async Task RefreshDiskCacheStatsAsync()
    {
        try
        {
            await _thumbnails.RefreshDiskCacheStatsAsync(_diskCacheCancellation.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
        catch (ObjectDisposedException)
        {
        }
        catch (Exception ex)
        {
            _applicationLog.Write($"⚠️ Disk önbelleği ölçümü başarısız: {ex.Message}");
        }
    }

    private void WriteSample()
    {
        if (!MetricLog.IsWriting) return;

        Collector.Refresh();
        MetricLog.WriteSample(Collector.Metrics.Snapshot());
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;

        _sampleTimer.Stop();
        _diskCacheTimer.Stop();
        _diskCacheCancellation.Cancel();
        _applicationLog.MessageWritten -= FileLog.Write;
        _indexLifecycle.ReconciliationStateChanged -= HandleReconciliationStateChanged;
        FileLog.Dispose();
        MetricLog.Dispose();
        _diskCacheCancellation.Dispose();
    }
}
