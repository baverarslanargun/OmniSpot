using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.UI.Views;

public partial class SettingsWindow
{
    private readonly ObservableCollection<string> _indexedFolders = new();

    private void InitializeIndexingSettings()
    {
        IndexMethodMft.IsChecked = _settings.IndexingMethod == IndexScanMethod.Mft;
        IndexMethodFolders.IsChecked = _settings.IndexingMethod != IndexScanMethod.Mft;
        var build = IndexBuildOptions.Normalize(_settings.IndexBuild);
        MftWorkerMode.SelectedIndex = (int)build.ScanMode;
        MftWorkerCount.Value = build.ScanWorkers;
        IndexSortAlgorithm.SelectedIndex = (int)build.SortMethod;
        IndexSortAdaptive.IsChecked = build.AdaptiveSort;
        IndexSortWorkers.Value = build.SortWorkers;
        _indexedFolders.Clear();
        var locations = new SettingsIndexedLocationProvider(_settings, new IndexedLocationProvider()).Resolve();
        foreach (var path in locations.RootPaths) _indexedFolders.Add(path);
        IndexedFoldersList.ItemsSource = _indexedFolders;
        IndexingValidation.Text = string.Empty;
    }

    private void AddIndexedFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "İndekslenecek klasörleri veya diski seç", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var selected in picker.FolderNames)
        {
            var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
            if (!_indexedFolders.Contains(path, StringComparer.OrdinalIgnoreCase)) _indexedFolders.Add(path);
        }
        IndexingValidation.Text = string.Empty;
    }

    private void RemoveIndexedFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var path in IndexedFoldersList.SelectedItems.Cast<string>().ToArray()) _indexedFolders.Remove(path);
    }

    private bool SaveIndexingSettings()
    {
        if (_indexedFolders.Count == 0)
        {
            IndexingValidation.Text = "İndekslenecek en az bir klasör veya disk seçin.";
            return false;
        }
        if (IndexMethodMft.IsChecked == true && _indexedFolders.Any(path => path.StartsWith(@"\\", StringComparison.Ordinal)))
        {
            IndexingValidation.Text = "Ağ klasörleri için klasör taramasını seçin. MFT yerel NTFS disklerde kullanılabilir.";
            return false;
        }
        _settings.IndexingMethod = IndexMethodMft.IsChecked == true ? IndexScanMethod.Mft : IndexScanMethod.SelectedFolders;
        _settings.IndexedFolders = _indexedFolders.ToList();
        _settings.IndexBuild = new((IndexScanWorkerMode)MftWorkerMode.SelectedIndex,
            (int)MftWorkerCount.Value, (IndexSortMethod)IndexSortAlgorithm.SelectedIndex,
            IndexSortAdaptive.IsChecked == true, (int)IndexSortWorkers.Value);
        if (!_settings.IndexBuild.IsValid)
        {
            IndexingValidation.Text = "Tarama ve sıralama işçi ayarlarını kontrol edin.";
            return false;
        }
        IndexingValidation.Text = string.Empty;
        return true;
    }

    private void ScheduleIndexRebuild()
    {
        var saved = _settingsApplication.Load();
        saved.IndexingMethod = _settings.IndexingMethod;
        saved.IndexedFolders = _settings.IndexedFolders?.ToList();
        saved.IndexBuild = _settings.IndexBuild;
        _settingsApplication.Save(saved);
        var persisted = _settingsApplication.Load();
        if (persisted.IndexingMethod != _settings.IndexingMethod || persisted.IndexBuild != _settings.IndexBuild ||
            !(persisted.IndexedFolders ?? []).SequenceEqual(_settings.IndexedFolders ?? [], StringComparer.OrdinalIgnoreCase))
            throw new IOException("Tarama yöntemi ve konumlar kaydedilemedi. İndeks yeniden oluşturulmadı.");
        _indexMaintenance.ScheduleRebuild();
    }
}
