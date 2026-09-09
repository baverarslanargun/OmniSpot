using System.IO;
using System.Reflection;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.UI.Views;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Views;

public sealed class ThumbnailSettingsWindowTests
{
    [Fact]
    public Task SaveDialogPersistsAndPublishesThumbnailPreferences() => RunSta(() =>
    {
        var original = new AppSettings();
        var store = new FakeSettings();
        var window = new SettingsWindow(original, store, new FakeMaintenance());
        AppSettings? published = null;
        Exception? error = null;
        window.SettingsChanged += (_, settings) => published = settings;
        window.Dispatcher.BeginInvoke(new Action(() =>
        {
            try
            {
                ((TextBox)window.FindName("ThumbnailCount")).Text = "5";
                ((TextBox)window.FindName("ThumbnailIdle")).Text = "120";
                typeof(SettingsWindow).GetMethod("Save_Click", BindingFlags.NonPublic | BindingFlags.Instance)!
                    .Invoke(window, new object[] { window, new RoutedEventArgs() });
            }
            catch (Exception ex) { error = ex; window.Close(); }
        }));
        var result = window.ShowDialog();
        Assert.Null(error);
        Assert.True(result);
        Assert.NotNull(store.Saved);
        Assert.Same(store.Saved, published);
        Assert.Equal(5, published!.ThumbnailCacheMaxCount);
        Assert.Equal(120, published.ThumbnailIdleSeconds);
        Assert.Equal(1000, original.ThumbnailCacheMaxCount);
    });

    [Fact]
    public Task InvalidManualInputIsRejectedAndDisabledInputDoesNotOverwritePreference() => RunSta(() =>
    {
        var original = new AppSettings { ThumbnailCacheMaxCount = 1000 };
        var window = CreateWindow(original);
        try
        {
            ((TextBox)window.FindName("ThumbnailCount")).Text = "9999999999";
            Assert.False(SaveDraft(window));
            Assert.NotEmpty(((TextBlock)window.FindName("ThumbnailValidation")).Text);
            ((CheckBox)window.FindName("ThumbnailEnabled")).IsChecked = false;
            Assert.True(SaveDraft(window));
            var draft = (AppSettings)typeof(SettingsWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.Equal(1000, draft.ThumbnailCacheMaxCount);
            Assert.False(draft.ThumbnailPreviewsEnabled);
            Assert.True(original.ThumbnailPreviewsEnabled);
            Assert.Equal(1000, original.ThumbnailCacheMaxCount);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task RatioUpdatesThePreviewWithoutAllocatingImagesAndKeepsManualPreference() => RunSta(() =>
    {
        var original = new AppSettings { ThumbnailCacheMaxCount = 300 };
        var window = CreateWindow(original);
        try
        {
            ((TextBox)window.FindName("ThumbnailCount")).Text = "invalid";
            ((ComboBox)window.FindName("ThumbnailMode")).SelectedIndex = 1;
            var slider = (Slider)window.FindName("ThumbnailRatio");
            slider.Value = Math.Min(0.02, slider.Maximum);
            var before = ((TextBlock)window.FindName("ThumbnailBudget")).Text;
            slider.Value = Math.Min(0.2, slider.Maximum);
            Assert.NotEqual(before, ((TextBlock)window.FindName("ThumbnailBudget")).Text);
            Assert.Equal(Visibility.Visible, ((StackPanel)window.FindName("ThumbnailRatioPanel")).Visibility);
            Assert.True(SaveDraft(window));
            var draft = (AppSettings)typeof(SettingsWindow).GetField("_settings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
            Assert.Equal(300, draft.ThumbnailCacheMaxCount);
            Assert.True(draft.ThumbnailCacheUseRamRatio);
            Assert.False(original.ThumbnailCacheUseRamRatio);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task NestedContextMenuUsesItsOwnPlacementTarget() => RunSta(() =>
    {
        var menu = new ContextMenu { PlacementTarget = new Border { Tag = @"C:\Images" } };
        var other = new MenuItem { Header = "Diğer" };
        var pin = new MenuItem { Header = "Thumbnailleri hızlı yükle" };
        other.Items.Add(pin);
        menu.Items.Add(other);
        Assert.Equal(@"C:\Images", MainWindow.ResolveThumbnailMenuPath(pin));
        Assert.Null(MainWindow.ResolveThumbnailMenuPath(new MenuItem()));
    });

    [Fact]
    public Task SettingsAndWarningRenderAtNarrowAndWideWidths() => RunSta(() =>
    {
        var window = CreateWindow(new());
        var warning = new ThumbnailPinConfirmationWindow(@"C:\Görseller\Uzun klasör adı\Tatil fotoğrafları");
        try
        {
            var card = (FrameworkElement)window.FindName("ThumbnailSettingsCard");
            Render(card, 540, "settings-manual");
            ((ComboBox)window.FindName("ThumbnailMode")).SelectedIndex = 1;
            Render(card, 540, "settings-ratio");
            Render(card, 425, "settings-ratio-narrow");
            Render((FrameworkElement)warning.Content, 420, "pin-warning");
        }
        finally { warning.Close(); window.Close(); }
    });

    private static SettingsWindow CreateWindow(AppSettings settings) => new(settings, new FakeSettings(), new FakeMaintenance());

    private static bool SaveDraft(SettingsWindow window) => (bool)typeof(SettingsWindow)
        .GetMethod("SaveThumbnailSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(window, null)!;

    private static void Render(FrameworkElement element, double width, string name)
    {
        element.Measure(new Size(width, double.PositiveInfinity));
        var size = new Size(width, Math.Ceiling(element.DesiredSize.Height));
        element.Arrange(new Rect(size));
        element.UpdateLayout();
        Assert.True(element.ActualHeight > 100);
        var bitmap = new RenderTargetBitmap((int)width, (int)size.Height, 96, 96, PixelFormats.Pbgra32);
        var background = new DrawingVisual();
        using (var context = background.RenderOpen()) context.DrawRectangle(Brushes.White, null, new Rect(size));
        bitmap.Render(background);
        bitmap.Render(element);
        var directory = Environment.GetEnvironmentVariable("OMNISPOT_UI_ARTIFACTS");
        if (string.IsNullOrWhiteSpace(directory)) return;
        Directory.CreateDirectory(directory);
        using var file = File.Create(Path.Combine(directory, name + ".png"));
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        encoder.Save(file);
    }

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
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }

    private sealed class FakeSettings : ISettingsApplicationService
    {
        public AppSettings? Saved { get; private set; }
        public AppSettings Load() => new();
        public void Save(AppSettings settings) => Saved = settings;
    }

    private sealed class FakeMaintenance : IIndexMaintenanceService
    {
        public IndexStorageStatus GetStatus() => new("test-index.db", false, 0);
        public bool OpenIndexFolder() => false;
        public void ScheduleRebuild() => throw new InvalidOperationException();
        public void ScheduleRestart() => throw new InvalidOperationException();
    }
}
