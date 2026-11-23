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

[Collection("Settings windows")]
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

    [Fact]
    public Task Ps5IsSelectableWhateverTheDiskMethods() => RunSta(() =>
    {
        var settings = new Settings();
        var window = new SettingsWindow(new AppSettings { IndexedFolders = [@"C:\Documents"] }, settings, new Maintenance());
        try
        {
            var algorithm = (ComboBox)window.FindName("IndexSortAlgorithm");
            var ps5 = (ComboBoxItem)algorithm.Items[2];
            Assert.Equal((int)IndexSortMethod.Ps5, algorithm.SelectedIndex);
            Assert.Equal("pS⁵", ps5.Content);
            Assert.True(ps5.IsEnabled);
            Assert.Equal(IndexVolumeMethod.Folders, Rows(window).Single().Method);
            Assert.True(Save(window));
            typeof(SettingsWindow).GetMethod("ScheduleIndexRebuild", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, null);
            Assert.Equal(IndexSortMethod.Ps5, settings.Load().IndexBuild.SortMethod);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task EachLocationShowsItsDiskMethodAndRowsOfOneDiskChangeTogether() => RunSta(() =>
    {
        var original = new AppSettings
        {
            IndexingMethod = IndexScanMethod.Mft,
            IndexedFolders = [@"C:\Documents", @"C:\Projects", @"\\server\share"],
            IndexVolumeMethods = new() { [@"C:\"] = IndexVolumeMethod.Mft }
        };
        var window = new SettingsWindow(original, new Settings(), new Maintenance());
        try
        {
            var rows = Rows(window);
            var panel = (FrameworkElement)window.FindName("MftOptionsPanel");
            Assert.Equal([IndexVolumeMethod.Mft, IndexVolumeMethod.Mft, IndexVolumeMethod.Automatic], rows.Select(row => row.Method).ToArray());
            Assert.False(rows[2].MftAvailable);
            rows[2].MethodIndex = (int)IndexVolumeMethod.Mft;
            Assert.Equal(IndexVolumeMethod.Automatic, rows[2].Method);
            rows[0].Method = IndexVolumeMethod.Folders;
            Assert.Equal(IndexVolumeMethod.Folders, rows[1].Method);
            Assert.Equal(Visibility.Collapsed, panel.Visibility);
            rows[1].Method = IndexVolumeMethod.Automatic;
            Assert.Equal(IndexVolumeMethod.Automatic, rows[0].Method);
            Assert.Equal(Visibility.Visible, panel.Visibility);
            Assert.True(Save(window));
            var draft = Draft(window);
            Assert.Equal(2, draft.IndexVolumeMethods!.Count);
            Assert.Equal(IndexVolumeMethod.Automatic, draft.IndexVolumeMethods[@"C:\"]);
            Assert.Equal(IndexVolumeMethod.Automatic, draft.IndexVolumeMethods[@"\\server\share\"]);
            Assert.Equal(IndexVolumeMethod.Mft, original.IndexVolumeMethods[@"C:\"]);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task AddedLocationsFollowTheirDiskOrStartAutomatic() => RunSta(() =>
    {
        var original = new AppSettings { IndexedFolders = [@"C:\Documents"], IndexVolumeMethods = new() { [@"C:\"] = IndexVolumeMethod.Folders } };
        var window = new SettingsWindow(original, new Settings(), new Maintenance());
        try
        {
            var add = typeof(SettingsWindow).GetMethod("AddIndexedLocation", BindingFlags.NonPublic | BindingFlags.Instance)!;
            add.Invoke(window, [@"C:\Projects\"]);
            add.Invoke(window, [@"E:\Yedek"]);
            add.Invoke(window, [@"c:\documents"]);
            var rows = Rows(window);
            Assert.Equal([@"C:\Documents", @"C:\Projects", @"E:\Yedek"], rows.Select(row => row.Path).ToArray());
            Assert.Equal([IndexVolumeMethod.Folders, IndexVolumeMethod.Folders, IndexVolumeMethod.Automatic], rows.Select(row => row.Method).ToArray());
            Assert.Equal(Visibility.Visible, ((FrameworkElement)window.FindName("MftOptionsPanel")).Visibility);
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
            Rows(window)[0].Method = IndexVolumeMethod.Mft;
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
                Assert.Equal(IndexVolumeMethod.Mft, Assert.Single(store.Load().IndexVolumeMethods!).Value);
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
            Rows(window)[0].Method = IndexVolumeMethod.Mft;
            Assert.Equal(original.IndexedFolders, Rows(window).Select(row => row.Path).ToArray());
            Assert.True(Save(window));
            var draft = Draft(window);
            Assert.Equal(IndexVolumeMethod.Mft, draft.IndexVolumeMethods![@"C:\"]);
            Assert.Equal(IndexVolumeMethod.Folders, draft.IndexVolumeMethods[@"D:\"]);
            Assert.Null(original.IndexVolumeMethods);
            Assert.Equal(original.IndexedFolders, draft.IndexedFolders);
            folders.SelectAll();
            typeof(SettingsWindow).GetMethod("RemoveIndexedFolder_Click", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(window, [window, new RoutedEventArgs()]);
            Assert.False(Save(window));
            Assert.NotEmpty(((TextBlock)window.FindName("IndexingValidation")).Text);
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task NetworkLocationsCannotChooseMftAndThePanelsRender() => RunSta(() =>
    {
        var window = new SettingsWindow(new AppSettings
        {
            IndexedFolders = [@"C:\", @"E:\", @"\\server\share"],
            IndexVolumeMethods = new() { [@"E:\"] = IndexVolumeMethod.Folders, [@"C:\"] = IndexVolumeMethod.Automatic }
        }, new Settings(), new Maintenance());
        try
        {
            var network = Rows(window)[2];
            Assert.False(network.MftAvailable);
            network.MethodIndex = (int)IndexVolumeMethod.Mft;
            Assert.NotEqual(IndexVolumeMethod.Mft, network.Method);
            Assert.True(Save(window));
            var folders = (ListBox)window.FindName("IndexedFoldersList");
            Render((FrameworkElement)((FrameworkElement)window.FindName("IndexSettingsHeading")).Parent, "indexing-locations");
            var selector = Descendant<ComboBox>((ListBoxItem)folders.ItemContainerGenerator.ContainerFromIndex(2));
            Assert.Equal((int)IndexVolumeMethod.Automatic, selector.SelectedIndex);
            Assert.False(((ComboBoxItem)selector.Items[1]).IsEnabled);
            var disk = Descendant<ComboBox>((ListBoxItem)folders.ItemContainerGenerator.ContainerFromIndex(1));
            Assert.Equal((int)IndexVolumeMethod.Folders, disk.SelectedIndex);
            disk.SelectedIndex = (int)IndexVolumeMethod.Automatic;
            Assert.Equal(IndexVolumeMethod.Automatic, Rows(window)[1].Method);
            Render((FrameworkElement)window.FindName("IndexingScopePanel"), "indexing-settings");
        }
        finally { window.Close(); }
    });

    [Fact]
    public Task MissingIndexCanBeCreatedFromSettings() => RunSta(() =>
    {
        var window = new SettingsWindow(new AppSettings { IndexedFolders = [@"C:\Belgeler"] }, new Settings(), new Maintenance());
        try
        {
            var update = typeof(SettingsWindow).GetMethod("UpdateIndexStatus", BindingFlags.NonPublic | BindingFlags.Instance)!;
            update.Invoke(window, [new IndexStorageStatus("test.db", false, 0)]);
            var button = (Button)window.FindName("RebuildIndexButton");
            Assert.True(button.IsEnabled);
            Assert.Equal("İndeksi oluştur", button.Content);
        }
        finally { window.Close(); }
    });

    [Theory]
    [InlineData(false, "Arama şu anda kullanılamıyor")]
    [InlineData(true, "Arama sonuçları güncel olmayabilir")]
    public void InactiveJournalExplainsTheImpactAndKeepsTechnicalDetailsAvailable(bool searchAvailable, string impact)
    {
        var issue = new IndexIssue(IndexIssueReason.JournalInactive, @"E:\", "FSCTL_QUERY_USN_JOURNAL",
            "FSCTL_QUERY_USN_JOURNAL başarısız: 1179", 1179);
        var health = new IndexHealth([issue], searchAvailable);

        var message = IndexIssuePresentation.Describe(health).Message;
        Assert.Contains(@"E:\", message);
        Assert.Contains(impact, message);
        Assert.Contains("günlüğü etkin değil", message);
        Assert.Contains("Klasör taraması", message);
        Assert.DoesNotContain("1179", message);
        var details = IndexIssuePresentation.FormatDetails(health);
        Assert.Contains(@"E:\", details);
        Assert.Contains("FSCTL_QUERY_USN_JOURNAL", details);
        Assert.Contains("1179", details);
        var summary = IndexIssuePresentation.Summarize(health);
        Assert.Equal(@"E:\", summary.Location);
        Assert.StartsWith(impact, summary.Effect);
        Assert.StartsWith("Ayarlar’da bu disk için", summary.NextStep);
        Assert.Equal($@"E:\ · {impact}. Windows’un dosya değişiklik günlüğü etkin değil. Ayarlar’da bu disk için Otomatik veya Klasör taraması’nı seçin ya da günlüğü etkinleştirin.", message);
    }

    [Fact]
    public void ASkippedLocationSaysTheOtherLocationsStillSearch()
    {
        var issue = new IndexIssue(IndexIssueReason.JournalInactive, @"E:\", "FSCTL_QUERY_USN_JOURNAL",
            "FSCTL_QUERY_USN_JOURNAL başarısız: 1179", 1179, LocationSkipped: true);

        var summary = IndexIssuePresentation.Summarize(new IndexHealth([issue], SearchAvailable: true));

        Assert.Equal("Bir konum indekslenemedi", summary.Title);
        Assert.Equal(@"E:\", summary.Location);
        Assert.Equal("Bu konum aramada yok; diğer konumlarda arama açık.", summary.Effect);
        Assert.StartsWith("Ayarlar’da bu disk için", summary.NextStep);
        Assert.Equal("Arama indeksi kullanılamıyor", IndexIssuePresentation.Summarize(new IndexHealth([issue], SearchAvailable: false)).Title);
    }

    [Fact]
    public Task TechnicalDetailsKeepTheCopyActionNextToClose() => RunSta(() =>
    {
        var issue = new IndexIssue(IndexIssueReason.JournalInactive, @"E:\", "FSCTL_QUERY_USN_JOURNAL",
            "FSCTL_QUERY_USN_JOURNAL başarısız: 1179", 1179);
        var health = new IndexHealth([issue], SearchAvailable: false, InitialStartup: true);
        var dialog = IndexIssuePresentation.CreateDetailsDialog(health);
        try
        {
            var buttons = dialog.ButtonRow.Children.OfType<Button>().Where(b => b.Visibility == System.Windows.Visibility.Visible).ToArray();
            Assert.Equal(new object[] { "Ayrıntıları kopyala", "Kapat" }, buttons.Select(b => b.Content));
            var field = ((StackPanel)dialog.Extra!).Children.OfType<TextBox>().Single();
            Assert.True(field.IsReadOnly);
            Assert.Equal(IndexIssuePresentation.FormatDetails(health), field.Text);
        }
        finally { dialog.Close(); }
    });

    private static IndexedLocationRow[] Rows(SettingsWindow window) =>
        ((ListBox)window.FindName("IndexedFoldersList")).Items.Cast<IndexedLocationRow>().ToArray();

    private static AppSettings Draft(SettingsWindow window) =>
        (AppSettings)typeof(SettingsWindow).GetField("_settings", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(window)!;

    private static T Descendant<T>(DependencyObject root) where T : DependencyObject
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            if (Descendant<T>(child) is { } nested) return nested;
        }
        return null!;
    }

    private static void Render(FrameworkElement panel, string name)
    {
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
            var bitmap = new RenderTargetBitmap(width * 2, (int)Math.Ceiling(panel.ActualHeight * 2), 192, 192, PixelFormats.Pbgra32);
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
            using var file = File.Create(Path.Combine(directory, $"{name}-{width}.png"));
            encoder.Save(file);
        }
    }

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
