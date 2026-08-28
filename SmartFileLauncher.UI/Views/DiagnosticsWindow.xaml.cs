using System.IO;
using System.Text;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.UI.Services;
using CheckBox = System.Windows.Controls.CheckBox;
using Clipboard = System.Windows.Clipboard;
using Color = System.Windows.Media.Color;
using FontFamily = System.Windows.Media.FontFamily;

namespace SmartFileLauncher.UI.Views;

public partial class DiagnosticsWindow : Window
{
    private const int MaxLogLines = 5000;

    private static readonly SolidColorBrush InkBrush = Frozen(0xE5, 0xE5, 0xEA);
    private static readonly SolidColorBrush MutedBrush = Frozen(0x86, 0x86, 0x8B);
    private static readonly SolidColorBrush GoodBrush = Frozen(0x30, 0xD1, 0x58);
    private static readonly SolidColorBrush WarningBrush = Frozen(0xFF, 0xB3, 0x40);
    private static readonly SolidColorBrush CriticalBrush = Frozen(0xFF, 0x6B, 0x6B);
    private static readonly FontFamily MonoFont = new("Consolas");

    private readonly ApplicationLog _applicationLog;
    private readonly DiagnosticsSession _session;
    private readonly AppSettings _settings;
    private readonly ISettingsApplicationService _settingsService;
    private readonly Func<IReadOnlyList<KeyValuePair<string, string>>> _stampProvider;
    private readonly string? _fixedOutputDirectory;

    private readonly DispatcherTimer _timer = new();
    private readonly Queue<string> _lines = new();
    private readonly Dictionary<(string Group, string Label), TextBlock> _valueBlocks = new();

    private long _renderedRevision = -1;
    private bool _suppressSettingChanges;

    public DiagnosticsWindow(
        ApplicationLog applicationLog,
        DiagnosticsSession session,
        AppSettings settings,
        ISettingsApplicationService settingsService,
        Func<IReadOnlyList<KeyValuePair<string, string>>> stampProvider,
        string? fixedOutputDirectory = null)
    {
        _applicationLog = applicationLog ?? throw new ArgumentNullException(nameof(applicationLog));
        _session = session ?? throw new ArgumentNullException(nameof(session));
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsService = settingsService ?? throw new ArgumentNullException(nameof(settingsService));
        _stampProvider = stampProvider ?? throw new ArgumentNullException(nameof(stampProvider));
        _fixedOutputDirectory = string.IsNullOrWhiteSpace(fixedOutputDirectory)
            ? null
            : Path.GetFullPath(fixedOutputDirectory);

        InitializeComponent();

        if (_fixedOutputDirectory != null)
        {
            ChooseDirectoryButton.IsEnabled = false;
            RememberDirectoryCheck.IsEnabled = false;
        }

        foreach (var message in _applicationLog.GetSnapshot())
        {
            _lines.Enqueue(message);
        }

        TrimAndRenderLog();
        _applicationLog.MessageWritten += HandleMessageWritten;

        CopyButton.Click += (_, __) => CopyEverything();
        ClearButton.Click += (_, __) => ClearLog();
        ChooseDirectoryButton.Click += (_, __) => ChooseDirectory();
        FileLoggingCheck.Checked += (_, __) => ApplyFileLogging(true);
        FileLoggingCheck.Unchecked += (_, __) => ApplyFileLogging(false);
        MetricLoggingCheck.Checked += (_, __) => ApplyMetricLogging(true);
        MetricLoggingCheck.Unchecked += (_, __) => ApplyMetricLogging(false);
        RememberDirectoryCheck.Checked += (_, __) => PersistRememberChoice(true);
        RememberDirectoryCheck.Unchecked += (_, __) => PersistRememberChoice(false);

        _suppressSettingChanges = true;
        FileLoggingCheck.IsChecked = _session.FileLog.IsWriting;
        MetricLoggingCheck.IsChecked = _session.MetricLog.IsWriting;
        RememberDirectoryCheck.IsChecked = _settings.RememberDiagnosticsLogDirectory;
        _suppressSettingChanges = false;

        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, __) => Refresh();

        Loaded += (_, __) =>
        {
            Refresh();
            _timer.Start();
        };
        Closed += (_, __) =>
        {
            _timer.Stop();
            _applicationLog.MessageWritten -= HandleMessageWritten;
        };
    }

    private static SolidColorBrush Frozen(byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private void HandleMessageWritten(string message)
    {
        if (Dispatcher.CheckAccess())
        {
            AppendLine(message);
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => AppendLine(message)));
    }

    private void AppendLine(string message)
    {
        var atEnd = LogScroll.ScrollableHeight - LogScroll.VerticalOffset < 4;
        _lines.Enqueue(message);

        if (_lines.Count > MaxLogLines)
        {
            TrimAndRenderLog();
        }
        else
        {
            LogText.Text = LogText.Text.Length == 0
                ? message
                : LogText.Text + Environment.NewLine + message;
        }

        if (atEnd)
        {
            LogScroll.ScrollToEnd();
        }
    }

    private void TrimAndRenderLog()
    {
        while (_lines.Count > MaxLogLines)
        {
            _lines.Dequeue();
        }

        LogText.Text = string.Join(Environment.NewLine, _lines);
    }

    private void ClearLog()
    {
        _lines.Clear();
        LogText.Text = string.Empty;
    }

    private void Refresh()
    {
        _session.Collector.Refresh();
        SyncMetrics();
        UpdatedText.Text = DateTime.Now.ToString("HH:mm:ss");
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var error = _session.FileLog.LastError ?? _session.MetricLog.LastError;
        if (error is { Length: > 0 })
        {
            LogStatusText.Text = $"hata: {error}";
            LogStatusText.Foreground = CriticalBrush;
            return;
        }

        var parts = new List<string>();
        var directory = _session.FileLog.CurrentFilePath is { Length: > 0 } logPath
            ? Path.GetDirectoryName(logPath)
            : _session.MetricLog.CurrentFilePath is { Length: > 0 } metricPath
                ? Path.GetDirectoryName(metricPath)
                : _fixedOutputDirectory ?? _settings.DiagnosticsLogDirectory;

        parts.Add(string.IsNullOrWhiteSpace(directory) ? "dizin seçilmedi" : directory);

        if (_session.FileLog.IsWriting)
        {
            parts.Add($"günlük {_session.FileLog.WrittenLines:N0} satır");
        }

        if (_session.MetricLog.IsWriting)
        {
            parts.Add($"sayaç {_session.MetricLog.WrittenRows:N0} satır");
        }

        LogStatusText.Text = string.Join("  ·  ", parts);
        LogStatusText.Foreground = MutedBrush;
    }

    private void ApplyFileLogging(bool enabled)
    {
        if (_suppressSettingChanges) return;

        if (!enabled)
        {
            _session.StopFileLogging();
            _settings.DiagnosticsLoggingEnabled = false;
            _settingsService.Save(_settings);
            UpdateStatus();
            return;
        }

        var directory = ResolveDirectory();
        if (directory == null)
        {
            SetCheck(FileLoggingCheck, false);
            return;
        }

        if (!_session.StartFileLogging(directory, _stampProvider()))
        {
            SetCheck(FileLoggingCheck, false);
            UpdateStatus();
            return;
        }

        _settings.DiagnosticsLoggingEnabled = true;
        RememberDirectory(directory);
        _settingsService.Save(_settings);
        UpdateStatus();
    }

    private void ApplyMetricLogging(bool enabled)
    {
        if (_suppressSettingChanges) return;

        if (!enabled)
        {
            _session.StopMetricLogging();
            _settings.DiagnosticsMetricLoggingEnabled = false;
            _settingsService.Save(_settings);
            UpdateStatus();
            return;
        }

        var directory = ResolveDirectory();
        if (directory == null)
        {
            SetCheck(MetricLoggingCheck, false);
            return;
        }

        if (!_session.StartMetricLogging(directory))
        {
            SetCheck(MetricLoggingCheck, false);
            UpdateStatus();
            return;
        }

        _settings.DiagnosticsMetricLoggingEnabled = true;
        RememberDirectory(directory);
        _settingsService.Save(_settings);
        UpdateStatus();
    }

    private string? ResolveDirectory()
    {
        if (_fixedOutputDirectory != null)
        {
            return _fixedOutputDirectory;
        }

        return string.IsNullOrWhiteSpace(_settings.DiagnosticsLogDirectory)
            ? PromptForDirectory()
            : _settings.DiagnosticsLogDirectory;
    }

    private void RememberDirectory(string directory)
    {
        if (_fixedOutputDirectory != null) return;

        if (_settings.RememberDiagnosticsLogDirectory)
        {
            _settings.DiagnosticsLogDirectory = directory;
        }
    }

    private void SetCheck(CheckBox box, bool value)
    {
        _suppressSettingChanges = true;
        box.IsChecked = value;
        _suppressSettingChanges = false;
    }

    private void ChooseDirectory()
    {
        if (_fixedOutputDirectory != null) return;

        var directory = PromptForDirectory();
        if (string.IsNullOrWhiteSpace(directory)) return;

        RememberDirectory(directory);
        _settingsService.Save(_settings);

        if (FileLoggingCheck.IsChecked == true)
        {
            _session.StartFileLogging(directory, _stampProvider());
        }

        if (MetricLoggingCheck.IsChecked == true)
        {
            _session.StartMetricLogging(directory);
        }

        UpdateStatus();
    }

    private string? PromptForDirectory()
    {
        if (_fixedOutputDirectory != null)
        {
            return _fixedOutputDirectory;
        }

        using var dialog = new System.Windows.Forms.FolderBrowserDialog
        {
            Description = "Tanılama dosyalarının yazılacağı dizin",
            UseDescriptionForTitle = true,
            SelectedPath = string.IsNullOrWhiteSpace(_settings.DiagnosticsLogDirectory)
                ? Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)
                : _settings.DiagnosticsLogDirectory
        };

        return dialog.ShowDialog() == System.Windows.Forms.DialogResult.OK
            ? dialog.SelectedPath
            : null;
    }

    private void PersistRememberChoice(bool remember)
    {
        if (_suppressSettingChanges || _fixedOutputDirectory != null) return;

        _settings.RememberDiagnosticsLogDirectory = remember;
        if (!remember)
        {
            _settings.DiagnosticsLogDirectory = string.Empty;
        }
        else if (_session.FileLog.CurrentFilePath is { Length: > 0 } path)
        {
            _settings.DiagnosticsLogDirectory = Path.GetDirectoryName(path) ?? string.Empty;
        }

        _settingsService.Save(_settings);
        UpdateStatus();
    }

    private void SyncMetrics()
    {
        var snapshot = _session.Collector.Metrics.Snapshot();
        var revision = _session.Collector.Metrics.Revision;

        if (revision != _renderedRevision)
        {
            RebuildMetrics(snapshot);
            _renderedRevision = revision;
            return;
        }

        foreach (var group in snapshot)
        {
            foreach (var reading in group.Readings)
            {
                if (_valueBlocks.TryGetValue((group.Title, reading.Label), out var block))
                {
                    block.Text = reading.Value;
                    block.Foreground = BrushFor(reading.Severity);
                }
            }
        }
    }

    private void RebuildMetrics(IReadOnlyList<DiagnosticsGroup> snapshot)
    {
        MetricsStack.Children.Clear();
        _valueBlocks.Clear();

        foreach (var group in snapshot)
        {
            MetricsStack.Children.Add(new TextBlock
            {
                Text = group.Title,
                FontFamily = MonoFont,
                FontSize = 10,
                Foreground = MutedBrush,
                Margin = new Thickness(0, MetricsStack.Children.Count == 0 ? 0 : 20, 0, 7)
            });

            foreach (var reading in group.Readings)
            {
                var row = new Grid { Margin = new Thickness(0, 0, 0, 4) };
                row.ColumnDefinitions.Add(
                    new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = reading.Label,
                    FontFamily = MonoFont,
                    FontSize = 11,
                    Foreground = MutedBrush,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(label, 0);
                row.Children.Add(label);

                var value = new TextBlock
                {
                    Text = reading.Value,
                    FontFamily = MonoFont,
                    FontSize = 13,
                    Foreground = BrushFor(reading.Severity),
                    TextAlignment = TextAlignment.Right,
                    VerticalAlignment = VerticalAlignment.Center
                };
                Grid.SetColumn(value, 1);
                row.Children.Add(value);

                MetricsStack.Children.Add(row);
                _valueBlocks[(group.Title, reading.Label)] = value;
            }
        }
    }

    private static SolidColorBrush BrushFor(DiagnosticsSeverity severity) => severity switch
    {
        DiagnosticsSeverity.Good => GoodBrush,
        DiagnosticsSeverity.Warning => WarningBrush,
        DiagnosticsSeverity.Critical => CriticalBrush,
        _ => InkBrush
    };

    private void CopyEverything()
    {
        var builder = new StringBuilder();
        builder.Append("OmniSpot tanılama · ")
            .AppendLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));

        foreach (var stamp in _stampProvider())
        {
            builder.Append(stamp.Key.PadRight(16)).Append(": ").AppendLine(stamp.Value);
        }

        foreach (var group in _session.Collector.Metrics.Snapshot())
        {
            builder.AppendLine().AppendLine(group.Title);
            foreach (var reading in group.Readings)
            {
                builder.Append("  ")
                    .Append(reading.Label.PadRight(22))
                    .AppendLine(reading.Value);
            }
        }

        builder.AppendLine().AppendLine("GÜNLÜK").AppendLine(LogText.Text);

        try
        {
            Clipboard.SetText(builder.ToString());
            _applicationLog.Write("📋 Tanılama panoya kopyalandı");
        }
        catch (Exception ex)
        {
            _applicationLog.Write($"⚠️ Panoya kopyalanamadı: {ex.Message}");
        }
    }
}
