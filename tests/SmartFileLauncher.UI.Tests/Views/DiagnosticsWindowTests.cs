using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.Tests.Services;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed class DiagnosticsWindowTests
{
    [Theory]
    [InlineData("[14:32:04.880] ❌ Açılamadı: a.lnk", "14:32:04.880", "❌ Açılamadı: a.lnk", DiagnosticsLogLevel.Error)]
    [InlineData("[14:32:03.441] ⚠️ Disk önbelleği ölçümü başarısız: x", "14:32:03.441", "⚠️ Disk önbelleği ölçümü başarısız: x", DiagnosticsLogLevel.Warning)]
    [InlineData("[14:31:52.118] === OmniSpot Başlatıldı ===", "14:31:52.118", "=== OmniSpot Başlatıldı ===", DiagnosticsLogLevel.Info)]
    [InlineData("[14:31:53.706] Arka planda ❌ geçen metin", "14:31:53.706", "Arka planda ❌ geçen metin", DiagnosticsLogLevel.Info)]
    [InlineData("Stack trace: at X", "", "Stack trace: at X", DiagnosticsLogLevel.Info)]
    [InlineData("[uzun bir köşeli parantez içeriği] ❌ x", "", "[uzun bir köşeli parantez içeriği] ❌ x", DiagnosticsLogLevel.Info)]
    [InlineData("", "", "", DiagnosticsLogLevel.Info)]
    public void LogLineSeparatesTimeAndLevel(string raw, string time, string message, DiagnosticsLogLevel level)
    {
        var line = DiagnosticsLogLine.Parse(raw);

        Assert.Equal(raw, line.Raw);
        Assert.Equal(time, line.Time);
        Assert.Equal(message, line.Message);
        Assert.Equal(level, line.Level);
    }

    [Fact]
    public Task LogListFollowsApplicationLogKeepsLimitAndClears() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Log.Write("ilk");
        fixture.Log.Write("❌ ikinci");
        var window = fixture.CreateWindow();
        try
        {
            var list = (ItemsControl)window.FindName("LogList");
            var empty = (TextBlock)window.FindName("EmptyLogText");
            var header = (TextBlock)window.FindName("UpdatedText");

            Assert.Equal(2, list.Items.Count);
            Assert.Equal(DiagnosticsLogLevel.Info, ((DiagnosticsLogLine)list.Items[0]).Level);
            Assert.Equal(DiagnosticsLogLevel.Error, ((DiagnosticsLogLine)list.Items[1]).Level);
            Assert.Equal(Visibility.Collapsed, empty.Visibility);
            Assert.Equal("2 satır", header.Text);

            for (var i = 0; i < DiagnosticsWindow.MaxLogLines; i++)
            {
                fixture.Log.Write($"satır {i}");
            }

            Assert.Equal(DiagnosticsWindow.MaxLogLines, list.Items.Count);
            Assert.EndsWith("] satır 0", ((DiagnosticsLogLine)list.Items[0]).Raw);
            Assert.EndsWith(
                $"] satır {DiagnosticsWindow.MaxLogLines - 1}",
                ((DiagnosticsLogLine)list.Items[list.Items.Count - 1]).Raw);

            Click(window, "ClearButton");

            Assert.Empty(list.Items);
            Assert.Equal(Visibility.Visible, empty.Visibility);
            Assert.Equal("0 satır", header.Text);

            fixture.Log.Write("⚠️ yeni");
            var only = Assert.Single(list.Items.Cast<DiagnosticsLogLine>());
            Assert.Equal(DiagnosticsLogLevel.Warning, only.Level);
            Assert.Equal(Visibility.Collapsed, empty.Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task CopyKeepsPreviousTextFormatAndConfirmsOnButton() => RunSta(() =>
    {
        using var fixture = new Fixture();
        fixture.Log.Write("⚠️ uyarı");
        fixture.Log.Write("bilgi");
        var window = fixture.CreateWindow();
        try
        {
            var metrics = fixture.Session.Collector.Metrics;
            metrics.Set("TEST", "durum", "iyi", DiagnosticsSeverity.Good);
            metrics.Set("TEST", "  alt", "5");
            var lines = fixture.Log.GetSnapshot();
            string? copied = null;
            window.ClipboardWriter = text => copied = text;
            var label = (TextBlock)window.FindName("CopyLabel");
            var nl = Environment.NewLine;

            Click(window, "CopyButton");

            Assert.NotNull(copied);
            Assert.StartsWith("OmniSpot tanılama · ", copied);
            Assert.Contains($"{"sürüm".PadRight(16)}: 1.0{nl}", copied);
            Assert.Contains(
                $"{nl}TEST{nl}  {"durum".PadRight(22)}iyi{nl}  {"  alt".PadRight(22)}5{nl}",
                copied);
            Assert.EndsWith($"{nl}GÜNLÜK{nl}{lines[0]}{nl}{lines[1]}{nl}", copied);
            Assert.Equal("Kopyalandı", label.Text);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("CopyDonePath")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("CopyGlyphPath")).Visibility);
            Assert.True(window.IsCopyFeedbackActive);
            Assert.EndsWith("📋 Tanılama panoya kopyalandı", fixture.Log.GetSnapshot()[^1]);

            window.EndCopyFeedback();

            Assert.Equal("Kopyala", label.Text);
            Assert.False(window.IsCopyFeedbackActive);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("CopyGlyphPath")).Visibility);

            window.ClipboardWriter = _ => throw new InvalidOperationException("pano meşgul");
            Click(window, "CopyButton");

            Assert.Equal("Kopyalanamadı", label.Text);
            Assert.Same(window.FindResource("Danger"), label.Foreground);
            var list = (ItemsControl)window.FindName("LogList");
            var last = (DiagnosticsLogLine)list.Items[list.Items.Count - 1];
            Assert.Equal(DiagnosticsLogLevel.Warning, last.Level);
            Assert.EndsWith("pano meşgul", last.Message);

            window.EndCopyFeedback();
            Assert.NotSame(window.FindResource("Danger"), label.Foreground);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task MetricsPromoteStatusIndentChildrenAndMarkOnlyAbnormalValues() => RunSta(() =>
    {
        using var fixture = new Fixture();
        var window = fixture.CreateWindow();
        try
        {
            var ink = window.FindResource("Ink");
            var ink2 = window.FindResource("Ink2");
            var warning = window.FindResource("Warning");
            var danger = window.FindResource("Danger");
            var metrics = fixture.Session.Collector.Metrics;
            metrics.Set("İNDEKS", "dosya", "12");
            metrics.Set("İNDEKS", "uzlaştırma", "çalışıyor %42", DiagnosticsSeverity.Warning);
            metrics.Set("İNDEKS", "  tur", "3");
            metrics.Set("İNDEKS", "durum", "iyi", DiagnosticsSeverity.Good);
            metrics.Set("ARAMA", "son süre", "38 ms", DiagnosticsSeverity.Good);

            window.SyncMetrics();

            var status = window.FindGroupStatus("İNDEKS")!;
            Assert.Equal("iyi", status.Text.Text);
            Assert.Same(ink2, status.Text.Foreground);
            Assert.Equal(Visibility.Visible, status.Panel.Visibility);
            Assert.Equal(Visibility.Collapsed, status.Glyph.Visibility);
            Assert.Null(window.FindMetric("İNDEKS", "durum"));
            Assert.Equal(Visibility.Collapsed, window.FindGroupStatus("ARAMA")!.Panel.Visibility);

            var child = window.FindMetric("İNDEKS", "  tur")!;
            Assert.Equal("tur", child.Label!.Text);
            Assert.Equal(12, child.Label.Margin.Left);
            Assert.Equal(0, window.FindMetric("İNDEKS", "dosya")!.Label!.Margin.Left);

            var reconciliation = window.FindMetric("İNDEKS", "uzlaştırma")!;
            Assert.Same(warning, reconciliation.Text.Foreground);
            Assert.Equal(Visibility.Visible, reconciliation.Glyph.Visibility);
            Assert.Same(window.FindResource("WarningGlyph"), reconciliation.Glyph.Data);
            Assert.Same(ink, window.FindMetric("ARAMA", "son süre")!.Text.Foreground);

            var stack = (StackPanel)window.FindName("MetricsStack");
            Assert.Equal(2, stack.Children.Count);
            Assert.Equal(0, ((FrameworkElement)stack.Children[0]).Margin.Top);
            Assert.Equal(20, ((FrameworkElement)stack.Children[1]).Margin.Top);
            Assert.Equal(4, ((Panel)stack.Children[0]).Children.Count);

            metrics.Set("İNDEKS", "durum", "InvalidOperationException", DiagnosticsSeverity.Critical);
            metrics.Set("İNDEKS", "uzlaştırma", "boşta");
            window.SyncMetrics();

            Assert.Same(status, window.FindGroupStatus("İNDEKS"));
            Assert.Equal("InvalidOperationException", status.Text.Text);
            Assert.Same(danger, status.Text.Foreground);
            Assert.Equal(Visibility.Visible, status.Glyph.Visibility);
            Assert.Same(window.FindResource("DangerGlyph"), status.Glyph.Data);
            Assert.Equal("boşta", reconciliation.Text.Text);
            Assert.Same(ink, reconciliation.Text.Foreground);
            Assert.Equal(Visibility.Collapsed, reconciliation.Glyph.Visibility);

            metrics.Set("ARAMA", "durum", "okunamadı", DiagnosticsSeverity.Warning);
            window.SyncMetrics();

            var searchStatus = window.FindGroupStatus("ARAMA")!;
            Assert.Equal(Visibility.Visible, searchStatus.Panel.Visibility);
            Assert.Same(warning, searchStatus.Text.Foreground);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task RecordingStripShowsDirectoryCountsAndErrors() => RunSta(() =>
    {
        using var fixture = new Fixture();
        var directory = fixture.CreateDirectory();
        fixture.Settings.DiagnosticsLogDirectory = directory;
        var window = fixture.CreateWindow();
        try
        {
            var status = (TextBlock)window.FindName("LogStatusText");
            var count = (TextBlock)window.FindName("FileLogCountText");
            var toggle = (CheckBox)window.FindName("FileLoggingCheck");

            Assert.Equal(directory, status.Text);
            Assert.Same(window.FindResource("Mono"), status.FontFamily);
            Assert.Equal(Visibility.Collapsed, count.Visibility);

            toggle.IsChecked = true;

            Assert.True(fixture.Session.FileLog.IsWriting);
            Assert.True(fixture.Store.Saved!.DiagnosticsLoggingEnabled);
            Assert.Equal(Visibility.Visible, count.Visibility);
            Assert.EndsWith(" satır", count.Text);
            Assert.Equal(directory, status.Text);

            toggle.IsChecked = false;

            Assert.False(fixture.Session.FileLog.IsWriting);
            Assert.False(fixture.Store.Saved!.DiagnosticsLoggingEnabled);
            Assert.Equal(Visibility.Collapsed, count.Visibility);

            var blocked = Path.Combine(directory, "dosya");
            File.WriteAllText(blocked, "x");
            fixture.Settings.DiagnosticsLogDirectory = blocked;
            toggle.IsChecked = true;

            Assert.False(fixture.Session.FileLog.IsWriting);
            Assert.False(toggle.IsChecked);
            Assert.StartsWith("hata: ", status.Text);
            Assert.Same(window.FindResource("Danger"), status.Foreground);
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("ErrorGlyphPath")).Visibility);
            Assert.Equal(Visibility.Collapsed, ((FrameworkElement)window.FindName("FolderGlyphPath")).Visibility);
        }
        finally
        {
            window.Close();
        }
    });

    [Fact]
    public Task DirectoryLabelCoversMissingAndFixedDirectories() => RunSta(() =>
    {
        using var fixture = new Fixture();
        var empty = fixture.CreateWindow();
        try
        {
            var status = (TextBlock)empty.FindName("LogStatusText");
            Assert.Equal("dizin seçilmedi", status.Text);
            Assert.Same(empty.FontFamily, status.FontFamily);
            Assert.True(((UIElement)empty.FindName("ChooseDirectoryButton")).IsEnabled);
        }
        finally
        {
            empty.Close();
        }

        var directory = fixture.CreateDirectory();
        var fixedWindow = fixture.CreateWindow(directory);
        try
        {
            Assert.Equal(Path.GetFullPath(directory), ((TextBlock)fixedWindow.FindName("LogStatusText")).Text);
            Assert.False(((UIElement)fixedWindow.FindName("ChooseDirectoryButton")).IsEnabled);
            Assert.False(((UIElement)fixedWindow.FindName("RememberDirectoryCheck")).IsEnabled);
        }
        finally
        {
            fixedWindow.Close();
        }
    });

    private static void Click(Window window, string name) =>
        ((ButtonBase)window.FindName(name)).RaiseEvent(new RoutedEventArgs(ButtonBase.ClickEvent));

    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() =>
        {
            try { action(); completion.SetResult(); }
            catch (Exception ex) { completion.SetException(ex); }
        }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(30));
    }

    private sealed class Fixture : IDisposable
    {
        private readonly List<string> _directories = new();

        public Fixture()
        {
            var index = new FakeIndexLifecycle();
            var thumbnails = new FakeThumbnailService();
            Session = new DiagnosticsSession(
                Log,
                new DiagnosticsCollector(index, thumbnails, 128, 1000),
                index,
                thumbnails,
                5);
        }

        public ApplicationLog Log { get; } = new();

        public AppSettings Settings { get; } = new();

        public SettingsStore Store { get; } = new();

        public DiagnosticsSession Session { get; }

        public DiagnosticsWindow CreateWindow(string? fixedDirectory = null) => new(
            Log,
            Session,
            Settings,
            Store,
            () => new[] { new KeyValuePair<string, string>("sürüm", "1.0") },
            fixedDirectory);

        public string CreateDirectory()
        {
            var directory = Path.Combine(
                Path.GetTempPath(),
                "omnispot-tanilama-test-" + Guid.NewGuid().ToString("N"));
            Directory.CreateDirectory(directory);
            _directories.Add(directory);
            return directory;
        }

        public void Dispose()
        {
            Session.Dispose();
            foreach (var directory in _directories)
            {
                try { Directory.Delete(directory, recursive: true); }
                catch (IOException) { }
                catch (UnauthorizedAccessException) { }
            }
        }
    }

    private sealed class SettingsStore : ISettingsApplicationService
    {
        public AppSettings? Saved { get; private set; }

        public AppSettings Load() => new();

        public void Save(AppSettings settings) => Saved = settings;
    }
}
