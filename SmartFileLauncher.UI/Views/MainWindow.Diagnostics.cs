using System.Reflection;
using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.UI.Services;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow {
    private DiagnosticsSession? _diagnostics;
    private DiagnosticsWindow? _diagnosticsWindow;

    private DiagnosticsSession Diagnostics =>
        _diagnostics ??= new DiagnosticsSession(
            _applicationLog,
            new DiagnosticsCollector(
                _indexLifecycle,
                _thumbnailService,
                THUMBNAIL_SIZE,
                MAX_FOLDER_ITEMS),
            _indexLifecycle,
            _thumbnailService,
            _appSettings.DiagnosticsMetricIntervalSeconds);

    private void InitializeDiagnostics() {
        var startup = DiagnosticsStartupOptions.Parse(Environment.GetCommandLineArgs());
        if (startup.Error != null) {
            Log($"⚠️ {startup.Error}");
        }

        var directory = startup.Directory ?? _appSettings.DiagnosticsLogDirectory;
        if (string.IsNullOrWhiteSpace(directory)) return;

        var writeLog = startup.IsRequested || _appSettings.DiagnosticsLoggingEnabled;
        var writeMetrics = startup.IsRequested || _appSettings.DiagnosticsMetricLoggingEnabled;
        if (!writeLog && !writeMetrics) return;

        if (startup.IsRequested) {
            Log($"🔬 Tanılama komut satırından açıldı: {directory}");
        }

        if (writeLog) {
            if (Diagnostics.StartFileLogging(directory, BuildDiagnosticsStamps())) {
                Log($"📝 Tanılama günlüğü: {Diagnostics.FileLog.CurrentFilePath}");
            } else {
                Log($"⚠️ Tanılama günlüğü açılamadı: {Diagnostics.FileLog.LastError}");
            }
        }

        if (writeMetrics) {
            if (Diagnostics.StartMetricLogging(directory)) {
                Log($"📈 Sayaç günlüğü: {Diagnostics.MetricLog.CurrentFilePath}");
            } else {
                Log($"⚠️ Sayaç günlüğü açılamadı: {Diagnostics.MetricLog.LastError}");
            }
        }
    }

    private IReadOnlyList<KeyValuePair<string, string>> BuildDiagnosticsStamps() {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "bilinmiyor";
        return new[] {
            new KeyValuePair<string, string>("sürüm", version),
            new KeyValuePair<string, string>("işletim sistemi", Environment.OSVersion.VersionString),
            new KeyValuePair<string, string>("çekirdek", Environment.ProcessorCount.ToString()),
            new KeyValuePair<string, string>("süreç", Environment.ProcessId.ToString()),
            new KeyValuePair<string, string>("64 bit", Environment.Is64BitProcess.ToString()),
            new KeyValuePair<string, string>("veritabanı", _indexLifecycle.DatabasePath),
            new KeyValuePair<string, string>("küçük resim", $"{THUMBNAIL_SIZE}×{THUMBNAIL_SIZE}"),
            new KeyValuePair<string, string>("klasör sınırı", MAX_FOLDER_ITEMS.ToString())
        };
    }

    private void ToggleDiagnosticsWindow() {
        if (_diagnosticsWindow != null) {
            _diagnosticsWindow.Activate();
            return;
        }

        var window = new DiagnosticsWindow(
            _applicationLog,
            Diagnostics,
            _appSettings,
            _settingsApplication,
            BuildDiagnosticsStamps);

        window.Closed += (_, __) => {
            _diagnosticsWindow = null;
            _diagnostics?.SetLiveViewActive(false);
        };
        _diagnosticsWindow = window;
        Diagnostics.SetLiveViewActive(true);
        window.Show();
    }

    private void RecordFolderMetrics(string folderPath, int itemCount, bool truncated) {
        Diagnostics.RecordFolder(folderPath, itemCount, truncated);
    }

    private void RecordSearchMetrics(int queryLength, TimeSpan duration, int resultCount) {
        Diagnostics.RecordSearch(queryLength, duration, resultCount);
    }

    private void ShutdownDiagnostics() {
        if (_diagnosticsWindow != null) {
            _diagnosticsWindow.Close();
            _diagnosticsWindow = null;
        }

        _diagnostics?.Dispose();
    }
}
