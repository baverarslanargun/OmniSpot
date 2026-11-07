using System.ComponentModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.UI.ViewModels;
using Clipboard = System.Windows.Clipboard;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListViewItem = System.Windows.Controls.ListViewItem;
using SelectionMode = System.Windows.Controls.SelectionMode;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private static readonly IReadOnlyDictionary<string, string> BatchMenuHeaders = new Dictionary<string, string>
    {
        ["CopyMenuItem"] = "Kopyala",
        ["CutMenuItem"] = "Kes",
        ["CopyPathMenuItem"] = "Yolu Kopyala",
        ["RenameMenuItem"] = "Yeniden Adlandır",
        ["DeleteMenuItem"] = "Sil",
        ["ZipMenuItem"] = "Sıkıştır (ZIP)",
        ["MoveToNewFolderMenuItem"] = "Yeni Klasöre Taşı",
        ["AddToShelfMenuItem"] = "Rafa Ekle"
    };

    private bool _archiveInProgress;

    private void InitializeBatchCommands()
    {
        ResultsList.SelectionChanged += (_, _) => UpdateSelectionBar();
        PreviewKeyDown += BatchPreviewKeyDown;
        foreach (var surface in new FrameworkElement[] { ResultsContainer, DesktopIconsScroll })
        {
            DependencyPropertyDescriptor
                .FromProperty(VisibilityProperty, typeof(UIElement))
                .AddValueChanged(surface, (_, _) => UpdateSelectionBar());
        }

        if (ResultsContainer.ContextMenu is { } resultsMenu)
        {
            var selectAll = new MenuItem
            {
                Header = "Tümünü Seç",
                Tag = "☑️",
                InputGestureText = "Ctrl+A",
                Style = FindResource("ModernMenuItem") as Style
            };
            selectAll.Click += ContextMenu_SelectAll;
            resultsMenu.Items.Insert(0, selectAll);
            resultsMenu.Items.Insert(1, new Separator { Style = FindResource("MenuSeparator") as Style });
        }
    }

    private bool SearchSurfaceVisible => ResultsContainer.Visibility == Visibility.Visible;

    private int SelectedItemCount => SearchSurfaceVisible ? ResultsList.SelectedItems.Count : _selectedDesktopItems.Count;

    private string[] SelectedItemPaths() =>
        SelectedFileItems(SearchSurfaceVisible).Select(GetFilePath).OfType<string>().ToArray();

    private IReadOnlyList<string> MenuTargets(string? path)
    {
        var selected = SelectedItemPaths();
        if (string.IsNullOrEmpty(path)) return selected;
        return selected.Length > 1 && selected.Contains(path, StringComparer.OrdinalIgnoreCase) ? selected : [path];
    }

    private IReadOnlyList<string> KeyboardTargets(string? fallback)
    {
        var selected = SelectedItemPaths();
        if (selected.Length > 1) return selected;
        return string.IsNullOrEmpty(fallback) ? selected : [fallback];
    }

    private void UpdateSelectionBar()
    {
        var count = SelectedItemCount;
        var show = count >= 2 && !_isPreparedForShutdown;
        if (show) SelectionCountText.Text = $"{count} öğe seçildi";
        var visible = SelectionBar.Visibility == Visibility.Visible;
        if (show == visible) return;
        SelectionBar.Visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (show) AnimatePanel(SelectionBar, 0, 6, 0);
    }

    private void BatchPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (KeyboardInDualPane) return;
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.A && modifiers == ModifierKeys.Control &&
            (Keyboard.FocusedElement is not TextBoxBase ||
             (ReferenceEquals(Keyboard.FocusedElement, SearchBox) && SearchBox.Text.Length == 0)))
        {
            e.Handled = true;
            SelectAllItems();
        }
        else if (e.Key == Key.C && modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            var targets = KeyboardTargets(_hoveredItemPath ?? _selectedItemPath);
            if (targets.Count == 0) return;
            e.Handled = true;
            CopyPaths(targets);
        }
        else if (e.Key == Key.Escape && modifiers == ModifierKeys.None && SelectedItemCount >= 2)
        {
            e.Handled = true;
            ClearItemSelection();
        }
    }

    private void SelectAllItems()
    {
        if (SearchSurfaceVisible)
        {
            if (ResultsList.SelectionMode != SelectionMode.Single) ResultsList.SelectAll();
        }
        else
        {
            foreach (var icon in _desktopIcons) SetTransferSelection(icon, true);
        }
        UpdateSelectionBar();
    }

    private void ClearItemSelection()
    {
        foreach (var selected in SelectedFileItems(SearchSurfaceVisible).ToArray())
            SetTransferSelection(selected, false);
        _selectionAnchor = null;
        UpdateSelectionBar();
    }

    private void UpdateBatchMenuItems(ContextMenu menu, string? path)
    {
        var count = MenuTargets(path).Count;
        foreach (var item in menu.Items.OfType<MenuItem>())
        {
            if (BatchMenuHeaders.TryGetValue(item.Name, out var header))
                item.Header = count > 1 ? $"{header} ({count} öğe)" : header;
        }

        var extract = menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "ExtractMenuItem");
        if (extract != null)
        {
            extract.Visibility = count == 1 && path != null &&
                string.Equals(Path.GetExtension(path), ".zip", StringComparison.OrdinalIgnoreCase)
                    ? Visibility.Visible
                    : Visibility.Collapsed;
        }
    }

    private void ContextMenu_CopyPath(object sender, RoutedEventArgs e) => CopyPaths(MenuTargets(GetPathFromContextMenu(sender)));

    private void ContextMenu_Compress(object sender, RoutedEventArgs e) => _ = CompressItemsAsync(MenuTargets(GetPathFromContextMenu(sender)));

    private void ContextMenu_Extract(object sender, RoutedEventArgs e)
    {
        var path = GetPathFromContextMenu(sender);
        if (!string.IsNullOrEmpty(path)) _ = ExtractArchiveAsync(path);
    }

    private void ContextMenu_MoveToNewFolder(object sender, RoutedEventArgs e) => MoveToNewFolder(MenuTargets(GetPathFromContextMenu(sender)));

    private void ContextMenu_SelectAll(object sender, RoutedEventArgs e) => SelectAllItems();

    private void SelectionCopy_Click(object sender, RoutedEventArgs e) => CopySelectionToClipboard(null, isCut: false);

    private void SelectionCut_Click(object sender, RoutedEventArgs e) => CopySelectionToClipboard(null, isCut: true);

    private void SelectionRename_Click(object sender, RoutedEventArgs e) => RenameItems(SelectedItemPaths());

    private void SelectionDelete_Click(object sender, RoutedEventArgs e) => DeleteItems(SelectedItemPaths());

    private void SelectionClear_Click(object sender, RoutedEventArgs e) => ClearItemSelection();

    private void SelectionMore_Click(object sender, RoutedEventArgs e)
    {
        var menu = new ContextMenu
        {
            Style = FindResource("ModernContextMenu") as Style,
            PlacementTarget = SelectionMoreButton,
            Placement = PlacementMode.Top
        };
        void Add(string header, string icon, string? gesture, Action action)
        {
            var item = new MenuItem
            {
                Header = header,
                Tag = icon,
                InputGestureText = gesture ?? string.Empty,
                Style = FindResource("ModernMenuItem") as Style
            };
            item.Click += (_, _) => action();
            menu.Items.Add(item);
        }

        Add("Yolu Kopyala", "🔤", "Ctrl+Shift+C", () => CopyPaths(SelectedItemPaths()));
        Add("Sıkıştır (ZIP)", "🗜️", null, () => _ = CompressItemsAsync(SelectedItemPaths()));
        Add("Yeni Klasöre Taşı", "🗂️", null, () => MoveToNewFolder(SelectedItemPaths()));
        foreach (var extra in SelectionMoreExtras()) menu.Items.Add(extra);
        menu.Items.Add(new Separator { Style = FindResource("MenuSeparator") as Style });
        Add("Tümünü Seç", "☑️", "Ctrl+A", SelectAllItems);
        menu.IsOpen = true;
    }

    private void CopyPaths(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        try
        {
            Clipboard.SetText(string.Join(Environment.NewLine, paths.Select(path => $"\"{path}\"")));
            NotifyFileTransfer(paths.Count == 1 ? "Yol kopyalandı." : $"{paths.Count} yol kopyalandı.");
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Panoya yazılamadı: {ex.Message}");
        }
    }

    private void DeleteItems(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (paths.Count == 1)
        {
            DeleteItem(paths[0]);
            return;
        }

        var confirmed = ModernDialog.Confirm(this, $"{paths.Count} öğe silinsin mi?",
            "Öğeler Geri Dönüşüm Kutusu'na taşınacak, oradan geri alabilirsiniz.", "Sil", DialogKind.Danger);
        if (!confirmed) return;

        var deleted = 0;
        var failures = new List<string>();
        foreach (var path in paths)
        {
            try
            {
                if (_fileOperations.DeleteToRecycleBin(path) != FileItemKind.Missing) deleted++;
                RemoveItemFromView(path);
                RemoveSearchResult(path);
            }
            catch (Exception ex)
            {
                failures.Add($"{Path.GetFileName(path)}: {ex.Message}");
            }
        }

        ReportBatch("🗑️", $"{deleted} öğe silindi", failures);
        UpdateSelectionBar();
    }

    private void RenameItems(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0) return;
        if (paths.Count == 1)
        {
            RenameItem(paths[0]);
            return;
        }

        var sources = paths
            .Select(path => new BatchRenameSource(path, _fileOperations.GetItemKind(path) == FileItemKind.Directory))
            .ToArray();
        var dialog = new BatchRenameDialog(sources) { Owner = this };
        if (dialog.ShowDialog() != true) return;

        var renamed = 0;
        var failures = new List<string>();
        foreach (var item in dialog.Plan.Where(item => item.Status == BatchRenameStatus.Ready))
        {
            try
            {
                var result = _fileOperations.Rename(item.SourcePath, item.NewName);
                ApplyRenameToViews(item.SourcePath, result.DestinationPath, item.NewName);
                renamed++;
            }
            catch (Exception ex)
            {
                failures.Add($"{item.OldName}: {ex.Message}");
            }
        }

        ReportBatch("✏️", $"{renamed} öğe yeniden adlandırıldı", failures);
    }

    private void ApplyRenameToViews(string oldPath, string newPath, string newName)
    {
        var icon = _desktopIcons.FirstOrDefault(item => PathsEqual(item.FullPath, oldPath));
        if (icon != null)
        {
            icon.Name = newName;
            icon.FullPath = newPath;
        }

        var index = IndexOfSearchResult(oldPath);
        if (index < 0) return;
        var old = _searchResults[index];
        var replacement = new SearchResultViewModel
        {
            Name = newName,
            FullPath = newPath,
            Score = old.Score,
            Icon = old.IsDirectory ? "folder" : GetFileIcon(newName),
            IsDirectory = old.IsDirectory
        };
        if (old.IsDirectory) replacement.SetFolderColors(newName);
        _searchResults[index] = replacement;
    }

    private void RemoveSearchResult(string path)
    {
        var index = IndexOfSearchResult(path);
        if (index >= 0) _searchResults.RemoveAt(index);
    }

    private int IndexOfSearchResult(string path)
    {
        for (var index = 0; index < _searchResults.Count; index++)
        {
            if (PathsEqual(_searchResults[index].FullPath, path)) return index;
        }
        return -1;
    }

    private void ReportBatch(string icon, string message, IReadOnlyList<string> failures)
    {
        Log($"{icon} {message}");
        foreach (var failure in failures) Log($"❌ {failure}");
        NotifyFileTransfer(failures.Count == 0
            ? $"{message}."
            : $"{message}; {failures.Count} öğede hata oldu (ayrıntı tanılama günlüğünde).");
    }

    private IEnumerable<System.Windows.Controls.Control> SelectionMoreExtras()
    {
        yield return CreateAddToShelfMenuItem();
        foreach (var item in DualPaneMenuExtras()) yield return item;
    }

    private static string? PathFromMenuTarget(ContextMenu menu) => menu.PlacementTarget switch
    {
        FrameworkElement { Tag: string path } => path,
        ListViewItem { Content: SearchResultViewModel result } => result.FullPath,
        FrameworkElement { DataContext: SearchResultViewModel result } => result.FullPath,
        FrameworkElement { DataContext: DesktopIconViewModel icon } => icon.FullPath,
        _ => null
    };

    private string? ArchiveFolderFor(IReadOnlyList<string> paths)
    {
        var parent = Path.GetDirectoryName(paths[0].TrimEnd('\\', '/'));
        return string.IsNullOrEmpty(parent)
            ? string.IsNullOrWhiteSpace(_desktopPath) ? null : _desktopPath
            : parent;
    }

    private async Task CompressItemsAsync(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || _measurementRun != null || _archiveInProgress) return;
        var folder = ArchiveFolderFor(paths);
        if (folder == null) return;
        var name = paths.Count == 1
            ? Path.GetFileNameWithoutExtension(paths[0].TrimEnd('\\', '/'))
            : "Arşiv";
        _archiveInProgress = true;
        NotifyFileTransfer($"{paths.Count} öğe sıkıştırılıyor…");
        try
        {
            var token = _lifetimeCancellation.Token;
            var archive = await Task.Run(() => ZipPackaging.Create(paths, folder, name, token), token);
            Log($"🗜️ Arşiv oluşturuldu: {archive}");
            NotifyFileTransfer($"{Path.GetFileName(archive)} oluşturuldu.");
            _ = ProcessFileChangeAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"❌ Sıkıştırma hatası: {ex.Message}");
            NotifyFileTransfer($"Sıkıştırılamadı: {ex.Message}");
        }
        finally
        {
            _archiveInProgress = false;
        }
    }

    private async Task ExtractArchiveAsync(string archivePath)
    {
        if (_measurementRun != null || _archiveInProgress) return;
        _archiveInProgress = true;
        NotifyFileTransfer($"{Path.GetFileName(archivePath)} çıkarılıyor…");
        try
        {
            var token = _lifetimeCancellation.Token;
            var folder = await Task.Run(() => ZipPackaging.Extract(archivePath, token), token);
            Log($"📦 Arşiv çıkarıldı: {folder}");
            NotifyFileTransfer($"“{Path.GetFileName(folder)}” klasörüne çıkarıldı.");
            _ = ProcessFileChangeAsync();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"❌ Arşiv çıkarma hatası: {ex.Message}");
            NotifyFileTransfer($"Çıkarılamadı: {ex.Message}");
        }
        finally
        {
            _archiveInProgress = false;
        }
    }

    private void MoveToNewFolder(IReadOnlyList<string> paths)
    {
        if (paths.Count == 0 || _measurementRun != null) return;
        var parent = Path.GetDirectoryName(paths[0].TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(parent)) return;

        string folder;
        try
        {
            folder = _fileOperations.CreateFolder(parent, "Yeni klasör");
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Klasör oluşturulamadı: {ex.Message}");
            return;
        }

        if (!PerformFileTransfer(paths.ToArray(), folder, move: true)) return;
        Log($"🗂️ {paths.Count} öğe yeni klasöre taşındı: {folder}");
        RenameItem(folder);
    }
}
