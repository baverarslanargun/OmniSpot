using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    internal static string? ResolveThumbnailMenuPath(object sender)
    {
        var current = sender;
        while (current is MenuItem item) current = item.Parent;
        if (current is not ContextMenu { PlacementTarget: FrameworkElement target }) return null;
        return ThumbnailMemoryPolicy.NormalizeFolder(target.Tag as string
            ?? (target.DataContext as DesktopIconViewModel)?.FullPath
            ?? (target.DataContext as SearchResultViewModel)?.FullPath
            ?? ((target as System.Windows.Controls.ListViewItem)?.Content as SearchResultViewModel)?.FullPath);
    }

    private void ThumbnailContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        if (sender is not ContextMenu menu) return;
        var other = menu.Items.OfType<MenuItem>().FirstOrDefault(item => Equals(item.Tag, "ThumbnailOther"));
        if (other == null) return;
        var path = ResolveThumbnailMenuPath(menu);
        other.Visibility = path != null && Directory.Exists(path) ? Visibility.Visible : Visibility.Collapsed;
        if (other.Items[0] is MenuItem pin)
        {
            var folders = ThumbnailMemoryPolicy.Configure(_appSettings, SystemMemoryReader.Read()).PinnedFolders;
            var exact = path != null && folders
                .Any(folder => string.Equals(ThumbnailMemoryPolicy.NormalizeFolder(folder), path, StringComparison.OrdinalIgnoreCase));
            var inherited = path != null && !exact && ThumbnailMemoryPolicy.IsPinned(path, folders);
            pin.IsChecked = exact || inherited;
            pin.IsEnabled = _appSettings.ThumbnailPreviewsEnabled && !inherited;
            pin.ToolTip = inherited ? "Üst klasör tarafından korunuyor. Bu tercihi ayarlardaki klasör listesinden değiştirebilirsiniz."
                : "Bu klasör ve alt klasörlerinde yüklenen küçük resimleri boşta kalınca da RAM'de tut";
        }
    }

    private void ContextMenu_PinThumbnails(object sender, RoutedEventArgs e)
    {
        var path = ResolveThumbnailMenuPath(sender);
        if (path == null || !Directory.Exists(path)) return;
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_appSettings))!;
        settings.ThumbnailPinnedFolders = ThumbnailMemoryPolicy.Configure(settings, SystemMemoryReader.Read()).PinnedFolders.ToList();
        var existing = settings.ThumbnailPinnedFolders.FindIndex(folder => string.Equals(folder, path, StringComparison.OrdinalIgnoreCase));
        if (existing >= 0)
            settings.ThumbnailPinnedFolders.RemoveAt(existing);
        else
        {
            if (!settings.HideThumbnailPinWarning)
            {
                var warning = new ThumbnailPinConfirmationWindow(path) { Owner = this };
                if (warning.ShowDialog() != true) return;
                settings.HideThumbnailPinWarning = warning.HideFutureWarnings;
            }
            settings.ThumbnailPinnedFolders.Add(path);
        }
        try
        {
            _settingsApplication.Save(settings);
            OnSettingsChanged(this, settings);
            Log(existing >= 0 ? "Klasörün küçük resimleri artık boşta kalınca temizlenecek."
                : "Klasörün küçük resimleri genel RAM sınırı içinde hızlı yükleme için korunacak.");
        }
        catch (Exception ex)
        {
            ModernDialog.Show(this, "Tercih kaydedilemedi", ex.Message, DialogKind.Danger);
        }
    }
}
