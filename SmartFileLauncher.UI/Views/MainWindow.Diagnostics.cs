using System.Reflection;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.UI.Services;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow {
    private static readonly TimeSpan ForcedLiveMemoryInterval = TimeSpan.FromSeconds(60);

    private readonly ApplicationStartupOptions _startupOptions;
    private readonly MeasurementRunLayout? _measurementRun;
    private DiagnosticsSession? _diagnostics;
    private DiagnosticsWindow? _diagnosticsWindow;

    private DiagnosticsSession Diagnostics =>
        _diagnostics ??= new DiagnosticsSession(
            _applicationLog,
            new DiagnosticsCollector(
                _indexLifecycle,
                _thumbnailService,
                THUMBNAIL_SIZE,
                MAX_FOLDER_ITEMS,
                // `--canli-yigin` ölçümü profilden bağımsız açar; verilmediğinde
                // ölçüm profilleri kendi varsayılan aralığıyla çalışmaya devam eder.
                forcedLiveMemoryInterval: _startupOptions.LiveHeapInterval
                    ?? (_startupOptions.Profile is null
                        ? null
                        : ForcedLiveMemoryInterval)),
            _indexLifecycle,
            _thumbnailService,
            _appSettings.DiagnosticsMetricIntervalSeconds,
            _startupOptions.Profile == MeasurementProfile.ProductionCopy
                ? DiagnosticPathRedactor.Redact
                : null);

    private void InitializeDiagnostics() {
        var startup = _startupOptions.Diagnostics;
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

        if (_measurementRun != null) {
            Log($"🧪 Ölçüm profili: {_startupOptions.ProfileName}");
            Diagnostics.RecordEvent(
                "profil hazır",
                _startupOptions.ProfileName);
        }
    }

    private IReadOnlyList<KeyValuePair<string, string>> BuildDiagnosticsStamps() {
        var version = Assembly.GetExecutingAssembly().GetName().Version?.ToString() ?? "bilinmiyor";
        var stamps = new List<KeyValuePair<string, string>> {
            new KeyValuePair<string, string>("sürüm", version),
            new KeyValuePair<string, string>("işletim sistemi", Environment.OSVersion.VersionString),
            new KeyValuePair<string, string>("çekirdek", Environment.ProcessorCount.ToString()),
            new KeyValuePair<string, string>("süreç", Environment.ProcessId.ToString()),
            new KeyValuePair<string, string>("64 bit", Environment.Is64BitProcess.ToString()),
            new KeyValuePair<string, string>("veritabanı", _indexLifecycle.DatabasePath),
            new KeyValuePair<string, string>("küçük resim", $"{THUMBNAIL_SIZE}×{THUMBNAIL_SIZE}"),
            new KeyValuePair<string, string>("klasör sınırı", MAX_FOLDER_ITEMS.ToString())
        };

        if (_measurementRun != null) {
            stamps.Add(new KeyValuePair<string, string>(
                "ölçüm profili",
                _startupOptions.ProfileName ?? "bilinmiyor"));
            stamps.Add(new KeyValuePair<string, string>("koşum kökü", _measurementRun.RunRoot));
            stamps.Add(new KeyValuePair<string, string>("veri kökü", _measurementRun.DataRoot));
            stamps.Add(new KeyValuePair<string, string>("ayar dizini", _measurementRun.SettingsDirectory));
            stamps.Add(new KeyValuePair<string, string>("ayar", _measurementRun.SettingsPath));
            stamps.Add(new KeyValuePair<string, string>("indeks dizini", _measurementRun.IndexDirectory));
            stamps.Add(new KeyValuePair<string, string>("index.db", _measurementRun.DatabasePath));
            if (_startupOptions.Profile == MeasurementProfile.EmptyProduction)
            {
                stamps.Add(new KeyValuePair<string, string>("index.db-wal", _measurementRun.DatabaseWalPath));
                stamps.Add(new KeyValuePair<string, string>("index.db-shm", _measurementRun.DatabaseShmPath));
            }
            if (_measurementRun.CorpusPath is { } corpusPath)
            {
                stamps.Add(new KeyValuePair<string, string>("corpus", corpusPath));
            }
            else
            {
                var indexedRootCount = new IndexedLocationProvider().Resolve().RootPaths.Count;
                stamps.Add(new KeyValuePair<string, string>(
                    "indexed roots",
                    $"<gizli-path> ({indexedRootCount} kök)"));
            }
            stamps.Add(new KeyValuePair<string, string>(
                "thumbnail cache",
                _measurementRun.ThumbnailCachePath));
            stamps.Add(new KeyValuePair<string, string>("sahiplik kilidi", _measurementRun.LeasePath));
            stamps.Add(new KeyValuePair<string, string>(
                "ölçüm yerleşimi",
                _startupOptions.Profile == MeasurementProfile.ProductionCopy
                    ? "preseeded production index/settings + fresh thumbnail cache"
                    : "isolated empty corpus + fresh thumbnail cache"));
        }

        return stamps;
    }

    private void RecordMeasurementEvent(
        string name,
        string? detail = null,
        double? numericValue = null) {
        if (_measurementRun == null) return;

        Diagnostics.RecordEvent(name, detail, numericValue);
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
            BuildDiagnosticsStamps,
            _startupOptions.Profile == MeasurementProfile.ProductionCopy
                ? _measurementRun?.RunRoot
                : null);

        window.Closed += (_, __) => {
            _diagnosticsWindow = null;
            _diagnostics?.SetLiveViewActive(false);
        };
        _diagnosticsWindow = window;
        Diagnostics.SetLiveViewActive(true);
        window.Show();
    }

    private void RecordFolderMetrics(string folderPath, int itemCount, bool truncated) {
        Diagnostics.RecordFolder(
            _startupOptions.Profile == MeasurementProfile.ProductionCopy
                ? "<gizli-path>"
                : folderPath,
            itemCount,
            truncated);
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
