using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartFileLauncher.UI.Services;
using Clipboard = System.Windows.Clipboard;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using Point = System.Windows.Point;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private const int MaxShelfItems = 500;
    private readonly ObservableCollection<ShelfItem> _shelf = [];
    private Point _shelfDragStart;
    private bool _shelfDragArmed;

    public sealed class ShelfItem(string path)
    {
        public string Path { get; } = path;
        public string Name { get; } = System.IO.Path.GetFileName(path.TrimEnd('\\', '/')) is { Length: > 0 } name ? name : path;
        public string Folder { get; } = System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? string.Empty;
    }

    private void InitializeShelf()
    {
        ShelfItems.ItemsSource = _shelf;
        _shelf.CollectionChanged += (_, _) => UpdateShelfChip();
        PreviewDragLeave += (_, _) => SetShelfDropHighlight(false);
        UpdateShelfChip();
    }

    private void AddToShelf(IReadOnlyList<string> paths)
    {
        var known = _shelf.Select(item => item.Path).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var added = 0;
        foreach (var path in paths.Select(WindowsFileTransfer.NormalizePath))
        {
            if (_shelf.Count >= MaxShelfItems || !known.Add(path)) continue;
            _shelf.Add(new ShelfItem(path));
            added++;
        }

        NotifyFileTransfer(added == 0
            ? "Bu öğeler zaten rafta."
            : $"{added} öğe rafa eklendi.");
        if (added > 0 && Animate) AnimatePanel(ShelfChip, 0, 8, 0.2);
    }

    private void UpdateShelfChip()
    {
        ShelfCountText.Text = _shelf.Count.ToString(System.Globalization.CultureInfo.CurrentCulture);
        ShelfChip.Visibility = _shelf.Count > 0 ? Visibility.Visible : Visibility.Collapsed;
        ShelfTitleText.Text = $"Raf · {_shelf.Count} öğe";
        if (_shelf.Count == 0) ShelfPopup.IsOpen = false;
    }

    private void ShelfChip_Click(object sender, RoutedEventArgs e)
    {
        if (ShelfPopup.IsOpen)
        {
            ShelfPopup.IsOpen = false;
            return;
        }

        var missing = _shelf.Where(item => !File.Exists(item.Path) && !Directory.Exists(item.Path)).ToArray();
        foreach (var item in missing) _shelf.Remove(item);
        if (missing.Length > 0) NotifyFileTransfer($"{missing.Length} öğe artık bulunamadığı için raftan çıkarıldı.");
        if (_shelf.Count == 0) return;

        var destination = GetPasteDestination();
        ShelfDestinationText.Text = destination == null
            ? "Hedef yok: önce bir klasöre git."
            : $"Hedef: {FolderDisplayName(destination)}";
        ShelfDestinationText.ToolTip = destination;
        ShelfCopyButton.IsEnabled = ShelfMoveButton.IsEnabled = destination != null;
        ShelfCopyButton.Opacity = ShelfMoveButton.Opacity = destination != null ? 1 : 0.45;
        ShelfPopup.HorizontalOffset = ShelfChip.ActualWidth - 342;
        ShelfPopup.IsOpen = true;
    }

    private void ShelfRemoveItem_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: ShelfItem item }) _shelf.Remove(item);
    }

    private void ShelfClear_Click(object sender, RoutedEventArgs e) => _shelf.Clear();

    private void ShelfCopy_Click(object sender, RoutedEventArgs e) => TransferShelf(move: false);

    private void ShelfMove_Click(object sender, RoutedEventArgs e) => TransferShelf(move: true);

    private void ShelfClipboard_Click(object sender, RoutedEventArgs e)
    {
        var paths = _shelf.Select(item => item.Path).ToArray();
        if (paths.Length == 0) return;
        try
        {
            Clipboard.SetDataObject(WindowsFileTransfer.CreateData(paths, DragDropEffects.Copy), copy: true);
            NotifyFileTransfer($"{paths.Length} öğe panoya alındı; Explorer'da da yapıştırabilirsin.");
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Panoya yazılamadı: {ex.Message}");
        }
    }

    private void TransferShelf(bool move)
    {
        var destination = GetPasteDestination();
        var paths = _shelf.Select(item => item.Path).ToArray();
        if (destination == null || paths.Length == 0) return;
        ShelfPopup.IsOpen = false;
        try
        {
            if (!PerformFileTransfer(paths, destination, move)) return;
            if (move) _shelf.Clear();
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Aktarım tamamlanamadı: {ex.Message}");
        }
    }

    private void ContextMenu_AddToShelf(object sender, RoutedEventArgs e) => AddToShelf(MenuTargets(GetPathFromContextMenu(sender)));

    private System.Windows.Controls.MenuItem CreateAddToShelfMenuItem()
    {
        var item = new System.Windows.Controls.MenuItem
        {
            Header = "Rafa Ekle",
            Tag = "🧺",
            Style = FindResource("ModernMenuItem") as Style
        };
        item.Click += (_, _) => AddToShelf(SelectedItemPaths());
        return item;
    }

    private bool TryHandleShelfDrag(DragEventArgs e, bool drop)
    {
        var overShelf = FileVisualParents(e.OriginalSource as DependencyObject).Contains(ShelfChip);
        SetShelfDropHighlight(overShelf && !drop);
        if (!overShelf) return false;

        var paths = WindowsFileTransfer.ReadPaths(e.Data);
        e.Effects = paths.Length > 0 && (e.AllowedEffects & DragDropEffects.Copy) != 0 ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
        _fileDragFeedback?.Hide();
        if (drop && paths.Length > 0)
        {
            ResetDrop();
            AddToShelf(paths);
        }
        return true;
    }

    private void SetShelfDropHighlight(bool active)
    {
        ShelfChip.BorderBrush = (System.Windows.Media.Brush)FindResource(active ? "ShelfAccentBrush" : "ShelfLineBrush");
    }

    private void ShelfDragHandle_MouseDown(object sender, MouseButtonEventArgs e)
    {
        _shelfDragStart = e.GetPosition(this);
        _shelfDragArmed = true;
    }

    private void ShelfDragHandle_MouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (!_shelfDragArmed || e.LeftButton != MouseButtonState.Pressed) return;
        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _shelfDragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _shelfDragStart.Y) < SystemParameters.MinimumVerticalDragDistance) return;

        _shelfDragArmed = false;
        var paths = _shelf.Select(item => item.Path).ToArray();
        if (paths.Length == 0) return;
        try
        {
            var result = DragDrop.DoDragDrop((DependencyObject)sender, WindowsFileTransfer.CreateData(paths),
                DragDropEffects.Copy | DragDropEffects.Move);
            if (result == DragDropEffects.Move || paths.All(path => !File.Exists(path) && !Directory.Exists(path)))
                _shelf.Clear();
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Sürükleme başlatılamadı: {ex.Message}");
        }
    }

    private void ShelfDragHandle_MouseUp(object sender, MouseButtonEventArgs e) => _shelfDragArmed = false;
}
