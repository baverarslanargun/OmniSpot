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

public sealed class IndexingSettingsWindowTests
{
    [Fact]
    public Task WorkerAndSortControlsRoundTripWithoutChangingOriginal() => RunSta(() =>
    {
        var original = new AppSettings { IndexedFolders = [@"C:\Documents"],
            IndexBuild = new(IndexScanWorkerMode.Manual, 7, IndexSortMethod.Merge, false, 6) };
        var settings = new Settings();
        var maintenance = new Maintenance();
        var window = new SettingsWindow(original, settings, maintenance);
        try
        {
            Assert.Equal(2, ((ComboBox)window.FindName("MftWorkerMode")).SelectedIndex);
            Assert.Equal(7, ((Slider)window.FindName("MftWorkerCount")).Value);
            Assert.Equal(1, ((ComboBox)window.FindName("IndexSortAlgorithm")).SelectedIndex);
            ((RadioButton)window.FindName("IndexMethodMft")).IsChecked = true;
            ((Slider)window.FindName("MftWorkerCount")).Value = 11;
            ((ComboBox)window.FindName("IndexSortAlgorithm")).SelectedIndex = 0;
            ((Slider)window.FindName("IndexSortWorkers")).Value = 3;
            Assert.True(Save(window));
            typeof(SettingsWindow).GetMethod("ScheduleIndexRebuild", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Assert.Equal(new IndexBuildOptions(IndexScanWorkerMode.Manual, 11, IndexSortMethod.Pivot, false, 3), settings.Load().IndexBuild);
            Assert.Equal(7, original.IndexBuild.ScanWorkers);
            Assert.True(maintenance.Rebuild);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public Task RebuildRequiresPersistedScopeAndPreservesOtherSavedPreferences(bool dropWrites) => RunSta(() =>
    {
        var store = new Settings { DropWrites = dropWrites };
        var maintenance = new Maintenance();
        var window = new SettingsWindow(new AppSettings { IndexedFolders = [@"C:\Documents"], ThumbnailCacheMaxCount = 123 }, store, maintenance);
        try
        {
            ((RadioButton)window.FindName("IndexMethodMft")).IsChecked = true;
            Assert.True(Save(window));
            var rebuild = typeof(SettingsWindow).GetMethod("ScheduleIndexRebuild", BindingFlags.NonPublic | BindingFlags.Instance)!;
            if (dropWrites)
            {
                Assert.IsType<IOException>(Assert.Throws<TargetInvocationException>(() => rebuild.Invoke(window, null)).InnerException);
                Assert.False(maintenance.Rebuild);
            }
            else
            {
                rebuild.Invoke(window, null);
                Assert.True(maintenance.Rebuild);
                Assert.Equal(IndexScanMethod.Mft, store.Load().IndexingMethod);
                Assert.Equal(new[] { @"C:\Documents" }, store.Load().IndexedFolders);
                Assert.Equal(new AppSettings().ThumbnailCacheMaxCount, store.Load().ThumbnailCacheMaxCount);
            }
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MethodIsIndependentFromScopeAndDraftDoesNotMutateOriginal() => RunSta(() =>
    {
        var original = new AppSettings { IndexedFolders = [@"C:\Documents", @"D:\Projects"] };
        var window = new SettingsWindow(original, new Settings(), new Maintenance());
        try
        {
            var folders = (ListBox)window.FindName("IndexedFoldersList");
            ((RadioButton)window.FindName("IndexMethodMft")).IsChecked = true;
            Assert.Equal(original.IndexedFolders, folders.Items.Cast<string>().ToArray());
            Assert.True(Save(window));
            var draft = (AppSettings)typeof(SettingsWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;
            Assert.Equal(IndexScanMethod.Mft, draft.IndexingMethod);
            Assert.Equal(IndexScanMethod.SelectedFolders, original.IndexingMethod);
            Assert.Equal(original.IndexedFolders, draft.IndexedFolders);
            folders.SelectAll();
            typeof(SettingsWindow).GetMethod("RemoveIndexedFolder_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, new RoutedEventArgs()]);
            Assert.False(Save(window));
            Assert.NotEmpty(((TextBlock)window.FindName("IndexingValidation")).Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MftRejectsNetworkScopeAndPanelRenders() => RunSta(() =>
    {
        var window = new SettingsWindow(new AppSettings { IndexedFolders = [@"\\server\share"] }, new Settings(), new Maintenance());
        try
        {
            ((RadioButton)window.FindName("IndexMethodMft")).IsChecked = true;
            Assert.False(Save(window));
            ((RadioButton)window.FindName("IndexMethodFolders")).IsChecked = true;
            Assert.True(Save(window));
            var panel = (FrameworkElement)window.FindName("IndexingScopePanel");
            panel.Margin = new Thickness(0);
            foreach (var width in new[] { 425, 540 })
            {
                panel.Measure(new Size(width, double.PositiveInfinity));
                panel.Arrange(new Rect(0, 0, width, panel.DesiredSize.Height));
                panel.UpdateLayout();
                Assert.True(panel.ActualHeight > 0);
                var directory = Environment.GetEnvironmentVariable("OMNISPOT_UI_ARTIFACTS");
                if (string.IsNullOrEmpty(directory)) continue;
                Directory.CreateDirectory(directory);
                var bitmap = new RenderTargetBitmap(width, (int)Math.Ceiling(panel.ActualHeight), 96, 96, PixelFormats.Pbgra32);
                var drawing = new DrawingVisual();
                using (var context = drawing.RenderOpen())
                {
                    var bounds = new Rect(0, 0, width, panel.ActualHeight);
                    context.DrawRectangle(Brushes.White, null, bounds);
                    context.DrawRectangle(new VisualBrush(panel), null, bounds);
                }
                bitmap.Render(drawing);
                var encoder = new PngBitmapEncoder();
                encoder.Frames.Add(BitmapFrame.Create(bitmap));
                using var file = File.Create(Path.Combine(directory, $"indexing-settings-{width}.png"));
                encoder.Save(file);
            }
        }
        finally { window.Close(); }
    });

    private static bool Save(SettingsWindow window) => (bool)typeof(SettingsWindow).GetMethod("SaveIndexingSettings", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null)!;
    private static Task RunSta(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var thread = new Thread(() => { try { action(); completion.SetResult(); } catch (Exception ex) { completion.SetException(ex); } }) { IsBackground = true };
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        return completion.Task.WaitAsync(TimeSpan.FromSeconds(15));
    }
    private sealed class Settings : ISettingsApplicationService
    {
        public bool DropWrites { get; init; }
        private string _json = "{}";
        public AppSettings Load() => System.Text.Json.JsonSerializer.Deserialize<AppSettings>(_json)!;
        public void Save(AppSettings settings) { if (!DropWrites) _json = System.Text.Json.JsonSerializer.Serialize(settings); }
    }
    private sealed class Maintenance : IIndexMaintenanceService
    {
        public bool Rebuild { get; private set; }
        public IndexStorageStatus GetStatus() => new("test.db", false, 0);
        public bool OpenIndexFolder() => false;
        public void ScheduleRebuild() => Rebuild = true;
        public void ScheduleRestart() => throw new InvalidOperationException();
    }
}
