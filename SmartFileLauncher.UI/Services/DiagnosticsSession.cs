using System.IO;
using System.Windows.Threading;
using SmartFileLauncher.Core.Diagnostics;

namespace SmartFileLauncher.UI.Services;

public sealed class DiagnosticsSession : IDisposable
{
    private readonly ApplicationLog _applicationLog;
    private readonly DispatcherTimer _sampleTimer = new();

    private bool _disposed;

    public DiagnosticsSession(
        ApplicationLog applicationLog,
        DiagnosticsCollector collector,
        int sampleIntervalSeconds)
    {
        _applicationLog = applicationLog ?? throw new ArgumentNullException(nameof(applicationLog));
        Collector = collector ?? throw new ArgumentNullException(nameof(collector));

        _applicationLog.MessageWritten += FileLog.Write;
        _sampleTimer.Interval = TimeSpan.FromSeconds(
            Math.Clamp(sampleIntervalSeconds, 1, 3600));
        _sampleTimer.Tick += (_, __) => WriteSample();
    }

    public DiagnosticsCollector Collector { get; }

    public DiagnosticsFileLog FileLog { get; } = new();

    public DiagnosticsMetricLog MetricLog { get; } = new();

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
            return false;
        }

        WriteSample();
        _sampleTimer.Start();
        return true;
    }

    public void StopMetricLogging()
    {
        _sampleTimer.Stop();
        MetricLog.Stop();
    }

    public void RecordFolder(string folderPath, int itemCount, bool truncated)
    {
        Collector.RecordFolder(folderPath, itemCount, truncated);

        if (!MetricLog.IsWriting) return;

        Collector.Refresh();
        MetricLog.WriteEvent("klasör açıldı", Path.GetFileName(folderPath), itemCount);
        MetricLog.WriteSample(Collector.Metrics.Snapshot());
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
        _applicationLog.MessageWritten -= FileLog.Write;
        FileLog.Dispose();
        MetricLog.Dispose();
    }
}
