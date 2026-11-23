using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Windows;
using Microsoft.Win32;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.UI.Views;

public partial class SettingsWindow
{
    private readonly ObservableCollection<IndexedLocationRow> _indexedFolders = new();

    private void InitializeIndexingSettings()
    {
        var build = IndexBuildOptions.Normalize(_settings.IndexBuild);
        MftWorkerMode.SelectedIndex = (int)build.ScanMode;
        MftWorkerCount.Value = build.ScanWorkers;
        IndexSortAlgorithm.SelectedIndex = (int)build.SortMethod;
        IndexSortAdaptive.IsChecked = build.AdaptiveSort;
        IndexSortWorkers.Value = build.SortWorkers;
        foreach (var row in _indexedFolders) row.PropertyChanged -= IndexedLocation_PropertyChanged;
        _indexedFolders.Clear();
        var locations = new SettingsIndexedLocationProvider(_settings, new IndexedLocationProvider()).Resolve();
        foreach (var path in locations.RootPaths) _indexedFolders.Add(CreateLocationRow(path, MethodFor(path)));
        IndexedFoldersList.ItemsSource = _indexedFolders;
        IndexingValidation.Text = string.Empty;
        UpdateMftOptions();
    }

    private IndexVolumeMethod MethodFor(string path)
    {
        try { return SettingsIndexedLocationProvider.MethodFor(_settings, path); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return IndexVolumeMethod.Automatic;
        }
    }

    private IndexedLocationRow CreateLocationRow(string path, IndexVolumeMethod method)
    {
        string volume;
        try { volume = SettingsIndexedLocationProvider.VolumeRoot(path); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException) { volume = path; }
        var row = new IndexedLocationRow(path, volume, method, IndexVolumeProbe.AllowsMft(volume));
        row.PropertyChanged += IndexedLocation_PropertyChanged;
        return row;
    }

    private void IndexedLocation_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (sender is not IndexedLocationRow changed || e.PropertyName != nameof(IndexedLocationRow.Method)) return;
        foreach (var row in _indexedFolders)
            if (!ReferenceEquals(row, changed) && string.Equals(row.Volume, changed.Volume, StringComparison.OrdinalIgnoreCase))
                row.Method = changed.Method;
        UpdateMftOptions();
    }

    private void UpdateMftOptions() =>
        MftOptionsPanel.Visibility = _indexedFolders.Any(row => row.MftAvailable && row.Method != IndexVolumeMethod.Folders)
            ? Visibility.Visible : Visibility.Collapsed;

    private void AddIndexedFolder_Click(object sender, RoutedEventArgs e)
    {
        var picker = new OpenFolderDialog { Title = "İndekslenecek klasörleri veya diski seç", Multiselect = true };
        if (picker.ShowDialog(this) != true) return;
        foreach (var selected in picker.FolderNames) AddIndexedLocation(selected);
        IndexingValidation.Text = string.Empty;
    }

    private void AddIndexedLocation(string selected)
    {
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(selected));
        if (_indexedFolders.Any(row => string.Equals(row.Path, path, StringComparison.OrdinalIgnoreCase))) return;
        var row = CreateLocationRow(path, IndexVolumeMethod.Automatic);
        var sibling = _indexedFolders.FirstOrDefault(item => string.Equals(item.Volume, row.Volume, StringComparison.OrdinalIgnoreCase));
        if (sibling is not null) row.Method = sibling.Method;
        _indexedFolders.Add(row);
        UpdateMftOptions();
    }

    private void RemoveIndexedFolder_Click(object sender, RoutedEventArgs e)
    {
        foreach (var row in IndexedFoldersList.SelectedItems.Cast<IndexedLocationRow>().ToArray())
        {
            row.PropertyChanged -= IndexedLocation_PropertyChanged;
            _indexedFolders.Remove(row);
        }
        UpdateMftOptions();
    }

    private bool SaveIndexingSettings()
    {
        if (_indexedFolders.Count == 0)
        {
            IndexingValidation.Text = "İndekslenecek en az bir klasör veya disk seçin.";
            return false;
        }
        _settings.IndexedFolders = _indexedFolders.Select(row => row.Path).ToList();
        _settings.IndexVolumeMethods = _indexedFolders
            .GroupBy(row => row.Volume, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.First().Volume, group => group.First().Method);
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
        saved.IndexVolumeMethods = _settings.IndexVolumeMethods?.ToDictionary(pair => pair.Key, pair => pair.Value);
        saved.IndexBuild = _settings.IndexBuild;
        _settingsApplication.Save(saved);
        var persisted = _settingsApplication.Load();
        if (persisted.IndexingMethod != _settings.IndexingMethod || persisted.IndexBuild != _settings.IndexBuild ||
            !(persisted.IndexedFolders ?? []).SequenceEqual(_settings.IndexedFolders ?? [], StringComparer.OrdinalIgnoreCase) ||
            !SameMethods(persisted.IndexVolumeMethods, _settings.IndexVolumeMethods))
            throw new IOException("Tarama yöntemi ve konumlar kaydedilemedi. İndeks yeniden oluşturulmadı.");
        _indexMaintenance.ScheduleRebuild();
    }

    private static bool SameMethods(IReadOnlyDictionary<string, IndexVolumeMethod>? left, IReadOnlyDictionary<string, IndexVolumeMethod>? right)
    {
        left ??= new Dictionary<string, IndexVolumeMethod>();
        right ??= new Dictionary<string, IndexVolumeMethod>();
        return left.Count == right.Count && left.All(pair => right.TryGetValue(pair.Key, out var method) && method == pair.Value);
    }
}
