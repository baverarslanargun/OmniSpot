using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.UI.Services;
using Clipboard = System.Windows.Clipboard;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using Point = System.Windows.Point;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private const double SinglePaneWidth = 680;
    private const double DualPaneWidth = 460;
    private const int DualPanePathBudget = 50;
    private readonly ObservableCollection<PaneEntry> _paneEntries = [];
    private string? _paneFolder;
    private CancellationTokenSource? _paneLoad;
    private Point _paneDragStart;
    private PaneEntry? _panePressed;
    private string _paneStatus = string.Empty;

    public sealed class PaneEntry(string name, string path, bool isDirectory)
    {
        public string Name { get; } = name;
        public string Path { get; } = path;
        public bool IsDirectory { get; } = isDirectory;
    }

    private bool DualPaneOpen => DualPane.Visibility == Visibility.Visible;

    private bool KeyboardInDualPane => DualPaneOpen && DualPane.IsKeyboardFocusWithin;

    private void InitializeDualPane()
    {
        DualPaneList.ItemsSource = _paneEntries;
        DualPaneList.SelectionChanged += (_, _) => UpdateDualPaneFooter();
        DualPaneList.MouseDoubleClick += DualPaneList_MouseDoubleClick;
        DualPaneList.KeyDown += DualPaneList_KeyDown;
        DualPaneList.PreviewMouseLeftButtonDown += DualPaneList_PreviewMouseDown;
        DualPaneList.PreviewMouseMove += DualPaneList_PreviewMouseMove;
        PreviewKeyDown += DualPanePreviewKeyDown;
    }

    private void DualPanePreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (e.Key == Key.D && Keyboard.Modifiers == (ModifierKeys.Control | ModifierKeys.Shift))
        {
            e.Handled = true;
            _ = SetDualPaneAsync(!DualPaneOpen);
        }
    }

    private void DualPaneMenuItem_Click(object sender, RoutedEventArgs e) => _ = SetDualPaneAsync(!DualPaneOpen);

    private void DualPaneClose_Click(object sender, RoutedEventArgs e) => _ = SetDualPaneAsync(false);

    private void DualPaneCopyLeft_Click(object sender, RoutedEventArgs e) => TransferPaneToLeft(move: false);

    private void DualPaneMoveLeft_Click(object sender, RoutedEventArgs e) => TransferPaneToLeft(move: true);

    private void ContextMenu_DualCopy(object sender, RoutedEventArgs e) => TransferToDualPane(MenuTargets(GetPathFromContextMenu(sender)), move: false);

    private void ContextMenu_DualMove(object sender, RoutedEventArgs e) => TransferToDualPane(MenuTargets(GetPathFromContextMenu(sender)), move: true);

    private async Task SetDualPaneAsync(bool open)
    {
        if (open == DualPaneOpen) return;
        if (open)
        {
            DualPane.Visibility = Visibility.Visible;
            Width = SinglePaneWidth + DualPaneWidth;
            KeepWindowOnScreen();
            await ShowPaneFolderAsync(_paneFolder ?? _currentFolderPath);
        }
        else
        {
            _paneLoad?.Cancel();
            DualPane.Visibility = Visibility.Collapsed;
            Width = SinglePaneWidth;
            _paneEntries.Clear();
        }

        DualPaneCheck.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        UpdateHeaderCorners();
        Log(open ? "🪟 Çift panel açıldı" : "🪟 Çift panel kapandı");
    }

    private void KeepWindowOnScreen()
    {
        var handle = new WindowInteropHelper(this).Handle;
        var transform = PresentationSource.FromVisual(this)?.CompositionTarget?.TransformFromDevice;
        if (handle == IntPtr.Zero || transform == null) return;
        var area = System.Windows.Forms.Screen.FromHandle(handle).WorkingArea;
        var topLeft = transform.Value.Transform(new Point(area.Left, area.Top));
        var bottomRight = transform.Value.Transform(new Point(area.Right, area.Bottom));
        if (Left + Width > bottomRight.X) Left = Math.Max(topLeft.X, bottomRight.X - Width);
    }

    private void UpdateHeaderCorners()
    {
        var tabs = TabStrip.Visibility == Visibility.Visible;
        var pane = DualPane.Visibility == Visibility.Visible;
        SearchHeader.CornerRadius = new CornerRadius(tabs ? 0 : 8, tabs || pane ? 0 : 8, 0, 0);
    }

    private async Task ShowPaneFolderAsync(string? folder)
    {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(ref _paneLoad, cancellation);
        previous?.Cancel();
        try
        {
            IReadOnlyList<PaneEntry> entries;
            var truncated = false;
            if (folder == null)
            {
                entries = _indexedRootPaths
                    .Where(Directory.Exists)
                    .Select(path => new PaneEntry(FolderDisplayName(path), path, true))
                    .ToArray();
            }
            else
            {
                var page = await _folderNavigation.OpenAsync(folder, MAX_FOLDER_ITEMS, ensureSynchronized: false, cancellation.Token);
                entries = page.Entries.Select(entry => new PaneEntry(entry.Name, entry.FullPath, entry.IsDirectory)).ToArray();
                truncated = page.IsTruncated;
            }

            if (cancellation.IsCancellationRequested || !DualPaneOpen) return;
            var sameFolder = PathsEqual(folder, _paneFolder);
            _paneFolder = folder;
            if (!sameFolder || !entries.Select(entry => entry.Path).SequenceEqual(_paneEntries.Select(entry => entry.Path), StringComparer.OrdinalIgnoreCase))
            {
                var selected = sameFolder
                    ? DualPaneList.SelectedItems.OfType<PaneEntry>().Select(entry => entry.Path).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                _paneEntries.Clear();
                foreach (var entry in entries) _paneEntries.Add(entry);
                foreach (var entry in _paneEntries.Where(entry => selected.Contains(entry.Path)))
                    DualPaneList.SelectedItems.Add(entry);
            }

            DualPaneEmpty.Visibility = entries.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
            _paneStatus = folder == null ? "Ana sayfa" : truncated ? $"{entries.Count:N0}+ öğe" : $"{entries.Count:N0} öğe";
            UpdateDualPanePath();
            UpdateDualPaneFooter();
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"❌ Karşı panel klasörü açılamadı: {ex.Message}");
            NotifyFileTransfer($"Klasör açılamadı: {ex.Message}");
        }
        finally
        {
            Interlocked.CompareExchange(ref _paneLoad, null, cancellation);
            cancellation.Dispose();
        }
    }

    private Task RefreshDualPaneAsync() =>
        DualPaneOpen ? ShowPaneFolderAsync(_paneFolder) : Task.CompletedTask;

    private void UpdateDualPanePath()
    {
        DualPanePath.Children.Clear();
        if (_paneFolder == null)
        {
            DualPanePath.Children.Add(new TextBlock
            {
                Text = "Ana sayfa",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Margin = new Thickness(6, 0, 0, 0),
                VerticalAlignment = VerticalAlignment.Center
            });
            return;
        }

        FillPathSegments(DualPanePath, _paneFolder, DualPanePathBudget, parentHint: false, target => _ = ShowPaneFolderAsync(target.Path));
    }

    private void UpdateDualPaneFooter()
    {
        var selected = DualPaneList.SelectedItems.Count;
        var destination = GetPasteDestination();
        DualPaneStatusText.Text = selected > 0 ? $"{selected} öğe seçili" : _paneStatus;
        var enabled = selected > 0 && destination != null && !PathsEqual(destination, _paneFolder);
        foreach (var button in new[] { DualPaneCopyLeftButton, DualPaneMoveLeftButton })
        {
            button.IsEnabled = enabled;
            button.Opacity = enabled ? 1 : 0.45;
            button.ToolTip = destination == null
                ? "Soldaki panelde bir klasör aç"
                : $"Hedef: {FolderDisplayName(destination)}";
        }
    }

    private string[] SelectedPanePaths() =>
        DualPaneList.SelectedItems.OfType<PaneEntry>().Select(entry => entry.Path).ToArray();

    private void TransferPaneToLeft(bool move)
    {
        var paths = SelectedPanePaths();
        var destination = GetPasteDestination();
        if (paths.Length == 0 || destination == null) return;
        try
        {
            if (PerformFileTransfer(paths, destination, move)) _ = RefreshDualPaneAsync();
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Aktarım tamamlanamadı: {ex.Message}");
        }
    }

    private void TransferToDualPane(IReadOnlyList<string> paths, bool move)
    {
        if (!DualPaneOpen || _paneFolder == null || paths.Count == 0) return;
        try
        {
            if (PerformFileTransfer(paths.ToArray(), _paneFolder, move)) _ = RefreshDualPaneAsync();
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Aktarım tamamlanamadı: {ex.Message}");
        }
    }

    private IEnumerable<System.Windows.Controls.Control> DualPaneMenuExtras()
    {
        if (!DualPaneOpen || _paneFolder == null) yield break;
        foreach (var (header, icon, move) in new[] { ("Karşı Panele Kopyala", "➡️", false), ("Karşı Panele Taşı", "⏩", true) })
        {
            var item = new MenuItem { Header = header, Tag = icon, Style = FindResource("ModernMenuItem") as Style };
            item.Click += (_, _) => TransferToDualPane(SelectedItemPaths(), move);
            yield return item;
        }
    }

    private void UpdateDualMenuItems(ContextMenu menu)
    {
        var visible = DualPaneOpen && _paneFolder != null ? Visibility.Visible : Visibility.Collapsed;
        foreach (var item in menu.Items.OfType<MenuItem>().Where(item => item.Name is "DualCopyMenuItem" or "DualMoveMenuItem"))
            item.Visibility = visible;
    }

    private string? DualPaneDropDestination(DependencyObject? source)
    {
        var row = FileVisualParents(source).OfType<ListBoxItem>().FirstOrDefault();
        return row?.DataContext is PaneEntry { IsDirectory: true } folder ? folder.Path : _paneFolder;
    }

    private PaneEntry? PaneEntryAt(object source) =>
        FileVisualParents(source as DependencyObject).OfType<ListBoxItem>().FirstOrDefault()?.DataContext as PaneEntry;

    private void DualPaneList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (PaneEntryAt(e.OriginalSource) is { } entry) OpenPaneEntry(entry);
    }

    private void OpenPaneEntry(PaneEntry entry)
    {
        if (entry.IsDirectory)
        {
            _ = ShowPaneFolderAsync(entry.Path);
            return;
        }

        try
        {
            _fileOperations.OpenFile(entry.Path);
        }
        catch (Exception ex)
        {
            ModernDialog.Show(this, "Açılamadı", ex.Message, DialogKind.Warning);
        }
    }

    private void DualPaneList_KeyDown(object sender, KeyEventArgs e)
    {
        var control = Keyboard.Modifiers == ModifierKeys.Control;
        var paths = SelectedPanePaths();
        switch (e.Key)
        {
            case Key.Enter when DualPaneList.SelectedItem is PaneEntry entry:
                OpenPaneEntry(entry);
                break;
            case Key.Back when _paneFolder != null:
                var parent = _folderNavigation.GetParentWithinRoots(_paneFolder, _indexedRootPaths);
                _ = ShowPaneFolderAsync(parent);
                break;
            case Key.Delete when paths.Length > 0:
                DeletePaneItems(paths);
                break;
            case Key.F2 when paths.Length > 0:
                RenameItems(paths);
                _ = RefreshDualPaneAsync();
                break;
            case Key.C or Key.X when control && paths.Length > 0:
                CopyPaneToClipboard(paths, cut: e.Key == Key.X);
                break;
            case Key.V when control && _paneFolder != null:
                PasteFromWindowsClipboard(_paneFolder);
                _ = RefreshDualPaneAsync();
                break;
            case Key.F2 or Key.Delete or Key.Enter or Key.Back:
            case Key.C or Key.X or Key.V when control:
                break;
            default:
                return;
        }
        e.Handled = true;
    }

    private void CopyPaneToClipboard(string[] paths, bool cut)
    {
        try
        {
            Clipboard.SetDataObject(WindowsFileTransfer.CreateData(paths, cut ? DragDropEffects.Move : DragDropEffects.Copy), copy: true);
            NotifyFileTransfer(cut ? $"{paths.Length} öğe kesildi." : $"{paths.Length} öğe kopyalandı.");
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Panoya yazılamadı: {ex.Message}");
        }
    }

    private void DeletePaneItems(string[] paths)
    {
        var title = paths.Length == 1 ? $"'{Path.GetFileName(paths[0])}' silinsin mi?" : $"{paths.Length} öğe silinsin mi?";
        if (!ModernDialog.Confirm(this, title,
                "Öğeler Geri Dönüşüm Kutusu'na taşınacak, oradan geri alabilirsiniz.", "Sil", DialogKind.Danger)) return;

        var failures = new List<string>();
        foreach (var path in paths)
        {
            try { _fileOperations.DeleteToRecycleBin(path); }
            catch (Exception ex) { failures.Add($"{Path.GetFileName(path)}: {ex.Message}"); }
        }
        ReportBatch("🗑️", $"{paths.Length - failures.Count} öğe silindi", failures);
        _ = RefreshDualPaneAsync();
    }

    private void DualPaneList_PreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        _panePressed = PaneEntryAt(e.OriginalSource);
        _paneDragStart = e.GetPosition(this);
    }

    private void DualPaneList_PreviewMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_panePressed is not { } pressed || e.LeftButton != MouseButtonState.Pressed || _transferInProgress) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _paneDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _paneDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _panePressed = null;
        var selected = SelectedPanePaths();
        var paths = selected.Contains(pressed.Path, StringComparer.OrdinalIgnoreCase) ? selected : [pressed.Path];
        try
        {
            DragDrop.DoDragDrop(DualPaneList, WindowsFileTransfer.CreateData(paths), DragDropEffects.Copy | DragDropEffects.Move);
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Sürükleme başlatılamadı: {ex.Message}");
        }
        finally
        {
            ResetDrop();
            _ = RefreshDualPaneAsync();
        }
    }

    private async void DualPaneSwap_Click(object sender, RoutedEventArgs e)
    {
        var left = _currentFolderPath;
        var right = _paneFolder;
        if (right == null) NavigateHome();
        else await NavigateToFolderAsync(right);
        await ShowPaneFolderAsync(left);
    }
}
