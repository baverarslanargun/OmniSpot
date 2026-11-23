using System.Collections.ObjectModel;
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
using FontFamily = System.Windows.Media.FontFamily;
using Orientation = System.Windows.Controls.Orientation;
using ShapePath = System.Windows.Shapes.Path;

namespace SmartFileLauncher.UI.Views;

public partial class DiagnosticsWindow : Window
{
    internal const int MaxLogLines = 5000;
    internal const string StatusLabel = "durum";

    private static readonly TimeSpan CopyFeedbackLength = TimeSpan.FromMilliseconds(1500);

    private readonly ApplicationLog _applicationLog;
    private readonly DiagnosticsSession _session;
    private readonly AppSettings _settings;
    private readonly ISettingsApplicationService _settingsService;
    private readonly Func<IReadOnlyList<KeyValuePair<string, string>>> _stampProvider;
    private readonly string? _fixedOutputDirectory;

    private readonly DispatcherTimer _timer = new();
    private readonly DispatcherTimer _copyFeedbackTimer = new();
    private readonly ObservableCollection<DiagnosticsLogLine> _logLines;
    private readonly Dictionary<(string Group, string Label), MetricView> _valueViews = new();
    private readonly Dictionary<string, MetricView> _statusViews = new();

    private readonly SolidColorBrush _ink;
    private readonly SolidColorBrush _ink2;
    private readonly SolidColorBrush _warning;
    private readonly SolidColorBrush _danger;
    private readonly FontFamily _monoFont;
    private readonly Geometry _warningGlyph;
    private readonly Geometry _dangerGlyph;

    private ScrollViewer? _logScroll;
    private DateTime? _refreshedAt;
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

        _ink = (SolidColorBrush)FindResource("Ink");
        _ink2 = (SolidColorBrush)FindResource("Ink2");
        _warning = (SolidColorBrush)FindResource("Warning");
        _danger = (SolidColorBrush)FindResource("Danger");
        _monoFont = (FontFamily)FindResource("Mono");
        _warningGlyph = (Geometry)FindResource("WarningGlyph");
        _dangerGlyph = (Geometry)FindResource("DangerGlyph");

        if (_fixedOutputDirectory != null)
        {
            ChooseDirectoryButton.IsEnabled = false;
            RememberDirectoryCheck.IsEnabled = false;
        }

        var snapshot = _applicationLog.GetSnapshot();
        var first = Math.Max(0, snapshot.Count - MaxLogLines);
        var lines = new List<DiagnosticsLogLine>(snapshot.Count - first);
        for (var i = first; i < snapshot.Count; i++)
        {
            lines.Add(DiagnosticsLogLine.Parse(snapshot[i]));
        }

        _logLines = new ObservableCollection<DiagnosticsLogLine>(lines);
        LogList.ItemsSource = _logLines;
        UpdateLogState();
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
        UpdateStatus();

        _timer.Interval = TimeSpan.FromSeconds(1);
        _timer.Tick += (_, __) => Refresh();
        _copyFeedbackTimer.Interval = CopyFeedbackLength;
        _copyFeedbackTimer.Tick += (_, __) => EndCopyFeedback();

        Loaded += (_, __) =>
        {
            Refresh();
            LogScroll?.ScrollToEnd();
            _timer.Start();
        };
        Closed += (_, __) =>
        {
            _timer.Stop();
            _copyFeedbackTimer.Stop();
            _applicationLog.MessageWritten -= HandleMessageWritten;
        };
    }

    internal Action<string> ClipboardWriter { get; set; } = Clipboard.SetText;

    internal bool IsCopyFeedbackActive => _copyFeedbackTimer.IsEnabled;

    internal MetricView? FindMetric(string group, string label) =>
        _valueViews.TryGetValue((group, label), out var view) ? view : null;

    internal MetricView? FindGroupStatus(string group) =>
        _statusViews.TryGetValue(group, out var view) ? view : null;

    private ScrollViewer? LogScroll
    {
        get
        {
            if (_logScroll != null) return _logScroll;

            LogList.ApplyTemplate();
            _logScroll = LogList.Template?.FindName("LogScroll", LogList) as ScrollViewer;
            return _logScroll;
        }
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
        var scroll = LogScroll;
        var atEnd = scroll == null || scroll.ScrollableHeight - scroll.VerticalOffset < 1;

        _logLines.Add(DiagnosticsLogLine.Parse(message));
        while (_logLines.Count > MaxLogLines)
        {
            _logLines.RemoveAt(0);
        }

        UpdateLogState();

        if (atEnd)
        {
            scroll?.ScrollToEnd();
        }
    }

    private void ClearLog()
    {
        _logLines.Clear();
        UpdateLogState();
    }

    private void UpdateLogState()
    {
        EmptyLogText.Visibility = _logLines.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeader();
    }

    private void UpdateHeader()
    {
        var count = $"{_logLines.Count:N0} satır";
        UpdatedText.Text = _refreshedAt is { } at
            ? $"Son yenileme {at:HH:mm:ss}  ·  {count}"
            : count;
    }

    internal void Refresh()
    {
        _session.Collector.Refresh();
        SyncMetrics();
        _refreshedAt = DateTime.Now;
        UpdateHeader();
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var error = _session.FileLog.LastError ?? _session.MetricLog.LastError;
        if (error is { Length: > 0 })
        {
            LogStatusText.Text = $"hata: {error}";
            LogStatusText.Foreground = _danger;
            LogStatusText.FontFamily = this.FontFamily;
            LogStatusText.ToolTip = LogStatusText.Text;
            FolderGlyphPath.Visibility = Visibility.Collapsed;
            ErrorGlyphPath.Visibility = Visibility.Visible;
        }
        else
        {
            var directory = _session.FileLog.CurrentFilePath is { Length: > 0 } logPath
                ? Path.GetDirectoryName(logPath)
                : _session.MetricLog.CurrentFilePath is { Length: > 0 } metricPath
                    ? Path.GetDirectoryName(metricPath)
                    : _fixedOutputDirectory ?? _settings.DiagnosticsLogDirectory;
            var chosen = !string.IsNullOrWhiteSpace(directory);

            LogStatusText.Text = chosen ? directory : "dizin seçilmedi";
            LogStatusText.Foreground = _ink2;
            LogStatusText.FontFamily = chosen ? _monoFont : this.FontFamily;
            LogStatusText.ToolTip = chosen ? directory : null;
            FolderGlyphPath.Visibility = Visibility.Visible;
            ErrorGlyphPath.Visibility = Visibility.Collapsed;
        }

        ShowCount(FileLogCountText, _session.FileLog.IsWriting, _session.FileLog.WrittenLines);
        ShowCount(MetricLogCountText, _session.MetricLog.IsWriting, _session.MetricLog.WrittenRows);
    }

    private static void ShowCount(TextBlock block, bool writing, long rows)
    {
        block.Visibility = writing ? Visibility.Visible : Visibility.Collapsed;
        block.Text = writing ? $"{rows:N0} satır" : string.Empty;
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

    internal void SyncMetrics()
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
                if (IsStatus(reading))
                {
                    if (_statusViews.TryGetValue(group.Title, out var status))
                    {
                        ApplyReading(status, reading, _ink2);
                    }

                    continue;
                }

                if (_valueViews.TryGetValue((group.Title, reading.Label), out var view))
                {
                    ApplyReading(view, reading, _ink);
                }
            }
        }
    }

    private void RebuildMetrics(IReadOnlyList<DiagnosticsGroup> snapshot)
    {
        MetricsStack.Children.Clear();
        _valueViews.Clear();
        _statusViews.Clear();

        foreach (var group in snapshot)
        {
            var section = new StackPanel
            {
                Margin = new Thickness(0, MetricsStack.Children.Count == 0 ? 0 : 20, 0, 0)
            };

            var header = new Grid { MinHeight = 20, Margin = new Thickness(0, 0, 0, 6) };
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            header.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            header.Children.Add(new TextBlock
            {
                Text = group.Title,
                FontSize = 11,
                FontWeight = FontWeights.SemiBold,
                Foreground = _ink2,
                VerticalAlignment = VerticalAlignment.Center
            });

            var status = CreateMetricView(12);
            status.Panel.Visibility = Visibility.Collapsed;
            Grid.SetColumn(status.Panel, 1);
            header.Children.Add(status.Panel);
            section.Children.Add(header);
            _statusViews[group.Title] = status;

            foreach (var reading in group.Readings)
            {
                if (IsStatus(reading))
                {
                    status.Panel.Visibility = Visibility.Visible;
                    ApplyReading(status, reading, _ink2);
                    continue;
                }

                var row = new Grid { MinHeight = 22 };
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
                row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

                var label = new TextBlock
                {
                    Text = reading.Label.Trim(),
                    FontSize = 12,
                    Foreground = _ink2,
                    VerticalAlignment = VerticalAlignment.Center,
                    TextTrimming = TextTrimming.CharacterEllipsis,
                    Margin = new Thickness(IsChild(reading.Label) ? 12 : 0, 0, 12, 0)
                };
                row.Children.Add(label);

                var value = CreateMetricView(13);
                value.Label = label;
                ApplyReading(value, reading, _ink);
                Grid.SetColumn(value.Panel, 1);
                row.Children.Add(value.Panel);

                section.Children.Add(row);
                _valueViews[(group.Title, reading.Label)] = value;
            }

            MetricsStack.Children.Add(section);
        }
    }

    private MetricView CreateMetricView(double fontSize)
    {
        var glyph = new ShapePath
        {
            Width = 12,
            Height = 12,
            Stretch = Stretch.Uniform,
            StrokeThickness = 1.4,
            StrokeStartLineCap = PenLineCap.Round,
            StrokeEndLineCap = PenLineCap.Round,
            StrokeLineJoin = PenLineJoin.Round,
            Margin = new Thickness(0, 0, 6, 0),
            VerticalAlignment = VerticalAlignment.Center,
            Visibility = Visibility.Collapsed
        };
        var text = new TextBlock
        {
            FontSize = fontSize,
            TextAlignment = TextAlignment.Right,
            VerticalAlignment = VerticalAlignment.Center
        };
        var panel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            VerticalAlignment = VerticalAlignment.Center
        };
        panel.Children.Add(glyph);
        panel.Children.Add(text);
        return new MetricView(panel, glyph, text);
    }

    private void ApplyReading(MetricView view, DiagnosticsReading reading, SolidColorBrush calm)
    {
        view.Text.Text = reading.Value;
        switch (reading.Severity)
        {
            case DiagnosticsSeverity.Warning:
                view.Text.Foreground = _warning;
                view.Glyph.Data = _warningGlyph;
                view.Glyph.Stroke = _warning;
                view.Glyph.Visibility = Visibility.Visible;
                break;
            case DiagnosticsSeverity.Critical:
                view.Text.Foreground = _danger;
                view.Glyph.Data = _dangerGlyph;
                view.Glyph.Stroke = _danger;
                view.Glyph.Visibility = Visibility.Visible;
                break;
            default:
                view.Text.Foreground = calm;
                view.Glyph.Visibility = Visibility.Collapsed;
                break;
        }
    }

    private static bool IsStatus(DiagnosticsReading reading) =>
        string.Equals(reading.Label, StatusLabel, StringComparison.Ordinal);

    private static bool IsChild(string label) =>
        label.Length > 0 && char.IsWhiteSpace(label[0]);

    internal string BuildCopyText()
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

        builder.AppendLine().AppendLine("GÜNLÜK").AppendLine(string.Join(
            Environment.NewLine,
            _logLines.Select(line => line.Raw)));
        return builder.ToString();
    }

    private void CopyEverything()
    {
        var text = BuildCopyText();

        try
        {
            ClipboardWriter(text);
            _applicationLog.Write("📋 Tanılama panoya kopyalandı");
            ShowCopyFeedback(true);
        }
        catch (Exception ex)
        {
            _applicationLog.Write($"⚠️ Panoya kopyalanamadı: {ex.Message}");
            ShowCopyFeedback(false);
        }
    }

    private void ShowCopyFeedback(bool copied)
    {
        CopyLabel.Text = copied ? "Kopyalandı" : "Kopyalanamadı";
        CopyGlyphPath.Visibility = copied ? Visibility.Collapsed : Visibility.Visible;
        CopyDonePath.Visibility = copied ? Visibility.Visible : Visibility.Collapsed;
        if (copied)
        {
            CopyLabel.ClearValue(TextBlock.ForegroundProperty);
        }
        else
        {
            CopyLabel.Foreground = _danger;
        }

        _copyFeedbackTimer.Stop();
        _copyFeedbackTimer.Start();
    }

    internal void EndCopyFeedback()
    {
        _copyFeedbackTimer.Stop();
        CopyLabel.Text = "Kopyala";
        CopyLabel.ClearValue(TextBlock.ForegroundProperty);
        CopyGlyphPath.Visibility = Visibility.Visible;
        CopyDonePath.Visibility = Visibility.Collapsed;
    }

    internal sealed class MetricView
    {
        public MetricView(StackPanel panel, ShapePath glyph, TextBlock text)
        {
            Panel = panel;
            Glyph = glyph;
            Text = text;
        }

        public StackPanel Panel { get; }

        public ShapePath Glyph { get; }

        public TextBlock Text { get; }

        public TextBlock? Label { get; set; }
    }
}
