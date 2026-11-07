using System.Collections.Specialized;
using System.IO;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Media.Media3D;
using System.Windows.Threading;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using ButtonBase = System.Windows.Controls.Primitives.ButtonBase;
using Clipboard = System.Windows.Clipboard;
using DataFormats = System.Windows.DataFormats;
using DragDrop = System.Windows.DragDrop;
using DragDropEffects = System.Windows.DragDropEffects;
using DragEventArgs = System.Windows.DragEventArgs;
using IDataObject = System.Windows.IDataObject;
using ListViewItem = System.Windows.Controls.ListViewItem;
using Point = System.Windows.Point;
using ScrollBar = System.Windows.Controls.Primitives.ScrollBar;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private readonly HashSet<DesktopIconViewModel> _selectedDesktopItems = [];
    private readonly HashSet<string> _cutPaths = new(StringComparer.OrdinalIgnoreCase);
    private readonly DispatcherTimer _transferFeedbackTimer = new() { Interval = TimeSpan.FromSeconds(3) };
    private object? _selectionAnchor;
    private FrameworkElement? _dragSource;
    private object? _pressedFileItem;
    private Point _dragStart;
    private bool _collapseListSelectionOnRelease;
    private bool _transferInProgress;
    private IDataObject? _dragData;
    private string[] _dragPaths = [];
    private DragDropEffects _dragPreferredEffect;
    private HwndSource? _fileClipboardSource;
    private uint _cutClipboardSequence;
    private FileDragFeedback? _fileDragFeedback;
    private string? _dragFeedbackDestination;

    private void InitializeFileTransfers()
    {
        if (_measurementRun != null)
        {
            ResultsList.SelectionMode = System.Windows.Controls.SelectionMode.Single;
            return;
        }

        AllowDrop = true;
        _fileDragFeedback = new FileDragFeedback(this);
        PreviewMouseLeftButtonDown += FileTransferMouseDown;
        PreviewMouseLeftButtonUp += FileTransferMouseUp;
        PreviewMouseMove += FileTransferMouseMove;
        PreviewKeyDown += FileTransferPreviewKeyDown;
        PreviewDragEnter += FileTransferDragOver;
        PreviewDragOver += FileTransferDragOver;
        PreviewDragLeave += (_, _) => ResetDrop();
        PreviewDrop += FileTransferDrop;
        ResultsList.SelectionChanged += (_, e) =>
        {
            foreach (var item in e.RemovedItems.OfType<SearchResultViewModel>()) item.IsTransferSelected = false;
            foreach (var item in e.AddedItems.OfType<SearchResultViewModel>()) item.IsTransferSelected = true;
        };
        _desktopIcons.CollectionChanged += DesktopTransferItemsChanged;
        _searchResults.CollectionChanged += (_, e) =>
        {
            if (e.NewItems != null)
                foreach (var item in e.NewItems.OfType<SearchResultViewModel>()) item.IsCut = _cutPaths.Contains(item.FullPath);
        };
        _transferFeedbackTimer.Tick += (_, _) =>
        {
            _transferFeedbackTimer.Stop();
            FileTransferLabel.Visibility = Visibility.Collapsed;
        };
        FileTransferOverlay.SizeChanged += (_, _) =>
        {
            if (_transferFeedbackTimer.IsEnabled)
                PositionFileTransferLabel(new Point(0, FileTransferOverlay.ActualHeight));
        };
    }

    private void DesktopTransferItemsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.Action == NotifyCollectionChangedAction.Reset)
        {
            foreach (var item in _selectedDesktopItems) item.IsTransferSelected = false;
            _selectedDesktopItems.Clear();
            if (_selectionAnchor is DesktopIconViewModel) _selectionAnchor = null;
        }
        if (e.OldItems != null && e.Action != NotifyCollectionChangedAction.Move)
            foreach (var item in e.OldItems.OfType<DesktopIconViewModel>())
                if (_selectedDesktopItems.Remove(item)) item.IsTransferSelected = false;
        if (e.NewItems != null)
            foreach (var item in e.NewItems.OfType<DesktopIconViewModel>()) item.IsCut = _cutPaths.Contains(item.FullPath);
        UpdateSelectionBar();
    }

    private static IEnumerable<DependencyObject> FileVisualParents(DependencyObject? source)
    {
        while (source != null)
        {
            yield return source;
            source = source is Visual or Visual3D
                ? VisualTreeHelper.GetParent(source)
                : LogicalTreeHelper.GetParent(source);
        }
    }

    private static (FrameworkElement? Element, object? Item) GetFileItem(DependencyObject? source)
    {
        foreach (var parent in FileVisualParents(source))
        {
            if (parent is FrameworkElement element &&
                (element is System.Windows.Controls.Button or ListViewItem) &&
                element.DataContext is DesktopIconViewModel or SearchResultViewModel)
                return (element, element.DataContext);
        }
        return (null, null);
    }

    private static string? GetFilePath(object? item) => item switch
    {
        DesktopIconViewModel desktop => desktop.FullPath,
        SearchResultViewModel result => result.FullPath,
        _ => null
    };

    private static string? GetFolderPath(object? item) => item switch
    {
        DesktopIconViewModel { IsDirectory: true } desktop => desktop.FullPath,
        SearchResultViewModel { IsDirectory: true } result => result.FullPath,
        _ => null
    };

    private IEnumerable<object> SelectedFileItems(bool search) => search
        ? ResultsList.SelectedItems.Cast<SearchResultViewModel>()
        : _selectedDesktopItems;

    private static bool IsTransferSelected(object item) => item switch
    {
        SearchResultViewModel result => result.IsTransferSelected,
        DesktopIconViewModel desktop => desktop.IsTransferSelected,
        _ => false
    };

    private void SetTransferSelection(object item, bool selected)
    {
        if (item is SearchResultViewModel result)
        {
            if (selected && !result.IsTransferSelected) ResultsList.SelectedItems.Add(result);
            if (!selected && result.IsTransferSelected) ResultsList.SelectedItems.Remove(result);
        }
        else if (item is DesktopIconViewModel desktop)
        {
            if (selected) _selectedDesktopItems.Add(desktop);
            else _selectedDesktopItems.Remove(desktop);
            desktop.IsTransferSelected = selected;
            UpdateSelectionBar();
        }
    }

    private void SelectFileItem(object item, ModifierKeys keys)
    {
        var search = item is SearchResultViewModel;
        var control = keys.HasFlag(ModifierKeys.Control);
        var shift = keys.HasFlag(ModifierKeys.Shift);
        if (!control)
        {
            foreach (var selected in SelectedFileItems(search).ToArray())
                SetTransferSelection(selected, false);
        }

        if (shift)
        {
            var items = search ? _searchResults.Cast<object>().ToList() : _desktopIcons.Cast<object>().ToList();
            var index = items.IndexOf(item);
            var anchor = items.IndexOf(_selectionAnchor!);
            if (anchor < 0) anchor = index;
            if (index >= 0)
                for (var i = Math.Min(anchor, index); i <= Math.Max(anchor, index); i++)
                    SetTransferSelection(items[i], true);
        }
        else
        {
            SetTransferSelection(item, !control || !IsTransferSelected(item));
            _selectionAnchor = item;
        }
    }

    private void FileTransferMouseDown(object sender, MouseButtonEventArgs e)
    {
        _collapseListSelectionOnRelease = false;
        (_dragSource, _pressedFileItem) = GetFileItem(e.OriginalSource as DependencyObject);
        if (_pressedFileItem is not { } item || _dragSource is not { } element)
        {
            if (FileVisualParents(e.OriginalSource as DependencyObject).Contains(FileContentArea) &&
                !FileVisualParents(e.OriginalSource as DependencyObject).Any(parent => parent is ScrollBar or ButtonBase))
            {
                foreach (var selected in SelectedFileItems(ResultsContainer.IsVisible).ToArray())
                    SetTransferSelection(selected, false);
                _selectionAnchor = null;
            }
            return;
        }

        _dragStart = e.GetPosition(this);
        var keys = Keyboard.Modifiers & (ModifierKeys.Control | ModifierKeys.Shift);
        if (keys != ModifierKeys.None)
        {
            element.Focus();
            SelectFileItem(item, keys);
            e.Handled = true;
        }
        else if (element is ListViewItem)
        {
            _selectionAnchor = item;
            if (IsTransferSelected(item) && ResultsList.SelectedItems.Count > 1)
            {
                _collapseListSelectionOnRelease = true;
                e.Handled = true;
            }
        }
        else if (!IsTransferSelected(item))
        {
            SelectFileItem(item, ModifierKeys.None);
        }
    }

    private void FileTransferMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_collapseListSelectionOnRelease && _pressedFileItem is { } item)
        {
            SelectFileItem(item, ModifierKeys.None);
            _dragSource?.Focus();
        }
        _collapseListSelectionOnRelease = false;
        _dragSource = null;
        _pressedFileItem = null;
    }

    private void FileTransferMouseMove(object sender, System.Windows.Input.MouseEventArgs e)
    {
        if (_dragSource is not { } source || _pressedFileItem is not { } item ||
            e.LeftButton != MouseButtonState.Pressed || _transferInProgress)
            return;

        var position = e.GetPosition(this);
        if (Math.Abs(position.X - _dragStart.X) < SystemParameters.MinimumHorizontalDragDistance &&
            Math.Abs(position.Y - _dragStart.Y) < SystemParameters.MinimumVerticalDragDistance)
            return;

        _collapseListSelectionOnRelease = false;
        _dragSource = null;
        _pressedFileItem = null;
        Mouse.Capture(null);
        e.Handled = true;
        source.GiveFeedback += FileTransferGiveFeedback;
        source.QueryContinueDrag += FileTransferQueryContinueDrag;
        try
        {
            var paths = NormalizeTransferPaths(GetTransferPaths(item));
            DragDrop.DoDragDrop(source, WindowsFileTransfer.CreateData(paths), DragDropEffects.Copy | DragDropEffects.Move);
            if (paths.Any(path => !File.Exists(path) && !Directory.Exists(path)))
                _ = ProcessFileChangeAsync();
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Sürükleme başlatılamadı: {ex.Message}");
        }
        finally
        {
            source.GiveFeedback -= FileTransferGiveFeedback;
            source.QueryContinueDrag -= FileTransferQueryContinueDrag;
            ResetDrop();
        }
    }

    private void FileTransferGiveFeedback(object sender, System.Windows.GiveFeedbackEventArgs e)
    {
        _fileDragFeedback?.Show(e.Effects, _dragFeedbackDestination);
        e.UseDefaultCursors = true;
        e.Handled = true;
    }

    private void FileTransferQueryContinueDrag(object sender, System.Windows.QueryContinueDragEventArgs e)
    {
        if (e.EscapePressed || !e.KeyStates.HasFlag(DragDropKeyStates.LeftMouseButton))
            _fileDragFeedback?.Hide();
    }

    private string[] GetTransferPaths(object item)
    {
        return IsTransferSelected(item)
            ? SelectedFileItems(item is SearchResultViewModel).Select(selected => GetFilePath(selected)!).ToArray()
            : [GetFilePath(item)!];
    }

    private bool HandleFileTransferKey(System.Windows.Input.KeyEventArgs e, string? fallbackPath)
    {
        if (Keyboard.FocusedElement is TextBoxBase)
            return false;

        if (e.Key is Key.C or Key.X)
            return CopySelectionToClipboard(fallbackPath, e.Key == Key.X);
        if (e.Key == Key.V)
        {
            PasteFromWindowsClipboard(GetPasteDestination());
            return true;
        }
        return false;
    }

    private void FileTransferPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.V || Keyboard.Modifiers != ModifierKeys.Control ||
            Keyboard.FocusedElement is not TextBoxBase || GetPasteDestination() is not { } destination)
            return;

        try
        {
            var sequence = GetClipboardSequenceNumber();
            var data = Clipboard.GetDataObject();
            if (data == null || data.GetDataPresent(DataFormats.UnicodeText) || data.GetDataPresent(DataFormats.Text))
                return;
            var paths = WindowsFileTransfer.ReadPaths(data);
            if (paths.Length == 0 || GetClipboardSequenceNumber() != sequence)
                return;

            e.Handled = true;
            PasteFileData(data, paths, sequence, destination);
        }
        catch (Exception ex)
        {
            if (e.Handled) NotifyFileTransfer($"Yapıştırılamadı: {ex.Message}");
        }
    }

    private bool CopySelectionToClipboard(string? fallbackPath, bool isCut, bool fromMenu = false)
    {
        if (_measurementRun != null)
        {
            if (string.IsNullOrEmpty(fallbackPath)) return false;
            CopyItemToInternalClipboard(fallbackPath, isCut);
            if (isCut && _desktopIcons.FirstOrDefault(item => item.FullPath == fallbackPath) is { } cut)
            {
                if (_cutItem != null) _cutItem.IsCut = false;
                _cutItem = cut;
                cut.IsCut = true;
            }
            return true;
        }

        var search = ResultsContainer.IsVisible;
        var focusedItem = GetFileItem(Keyboard.FocusedElement as DependencyObject).Item;
        var selected = SelectedFileItems(search).Select(item => GetFilePath(item)!).ToArray();
        if (search && !fromMenu && focusedItem == null)
            fallbackPath = null;
        var paths = fromMenu && fallbackPath != null && !selected.Contains(fallbackPath, StringComparer.OrdinalIgnoreCase)
            ? [fallbackPath]
            : selected.Length > 0 ? selected : [GetFilePath(focusedItem) ?? fallbackPath ?? string.Empty];
        if (paths.Length == 1 && paths[0].Length == 0) return false;
        if (_transferInProgress) return true;

        try
        {
            paths = NormalizeTransferPaths(paths);
            var effect = isCut ? DragDropEffects.Move : DragDropEffects.Copy;
            Clipboard.SetDataObject(WindowsFileTransfer.CreateData(paths, effect), copy: true);
            _cutClipboardSequence = GetClipboardSequenceNumber();
            SetCutPaths(isCut ? paths : []);
            NotifyFileTransfer(isCut ? $"{paths.Length} öğe kesildi." : $"{paths.Length} öğe kopyalandı.");
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Panoya yazılamadı: {ex.Message}");
        }
        return true;
    }

    private static string[] NormalizeTransferPaths(IEnumerable<string> paths) =>
        paths.Select(WindowsFileTransfer.NormalizePath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    private void PasteFromWindowsClipboard(string? destination)
    {
        if (_measurementRun != null)
        {
            PasteFromInternalClipboard();
            return;
        }
        if (_transferInProgress) return;
        if (destination == null)
        {
            NotifyFileTransfer("Yapıştırmak için bir hedef klasör seç.");
            return;
        }

        try
        {
            var sequence = GetClipboardSequenceNumber();
            var data = Clipboard.GetDataObject();
            var paths = WindowsFileTransfer.ReadPaths(data);
            if (paths.Length == 0 || data == null)
            {
                NotifyFileTransfer("Panoda dosya veya klasör yok.");
                return;
            }

            PasteFileData(data, paths, sequence, destination);
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Yapıştırılamadı: {ex.Message}");
        }
    }

    private void PasteFileData(IDataObject data, string[] paths, uint sequence, string destination)
    {
        var move = WindowsFileTransfer.ReadPreferredEffect(data) == DragDropEffects.Move;
        if (!PerformFileTransfer(paths, destination, move) || !move)
            return;

        WindowsFileTransfer.ReportMove(data, paste: true);
        ClearClipboardIfUnchanged(sequence);
        SetCutPaths([]);
    }

    private string? GetPasteDestination()
    {
        if (ResultsContainer.IsVisible)
            return ResultsList.SelectedItems.Count == 1 ? GetFolderPath(ResultsList.SelectedItem) : null;
        return _currentFolderPath ?? (string.IsNullOrWhiteSpace(_desktopPath) ? null : _desktopPath);
    }

    private string? GetDropDestination(DependencyObject? source)
    {
        if (DualPaneOpen && FileVisualParents(source).Contains(DualPane))
            return FileVisualParents(source).Any(parent => parent is ScrollBar) ? null : DualPaneDropDestination(source);
        if (!FileVisualParents(source).Contains(FileContentArea) ||
            FileVisualParents(source).Any(parent => parent is ScrollBar))
            return null;

        var item = GetFileItem(source).Item;
        if (item != null)
            return GetFolderPath(item);
        return ResultsContainer.IsVisible || FileVisualParents(source).Any(parent => parent is ButtonBase)
            ? null
            : _currentFolderPath;
    }

    private (string? Destination, DragDropEffects Effect) ResolveDrop(DragEventArgs e)
    {
        if (_transferInProgress || GetDropDestination(e.OriginalSource as DependencyObject) is not { } target)
            return (null, DragDropEffects.None);
        if (!ReferenceEquals(_dragData, e.Data))
        {
            _dragPaths = [];
            _dragPreferredEffect = DragDropEffects.None;
            _dragData = e.Data;
            try
            {
                _dragPaths = WindowsFileTransfer.ReadPaths(e.Data);
                if (_dragPaths.Length > 0)
                    _dragPreferredEffect = WindowsFileTransfer.ReadPreferredEffect(e.Data);
            }
            catch (Exception ex) when (ex is ArgumentException or IOException or ExternalException)
            {
                _dragPaths = [];
            }
        }

        try
        {
            var destination = WindowsFileTransfer.NormalizePath(target);
            return (destination, WindowsFileTransfer.ChooseDropEffect(
                _dragPaths, destination, e.KeyStates, e.AllowedEffects, _dragPreferredEffect));
        }
        catch (Exception ex) when (ex is ArgumentException or IOException)
        {
            return (null, DragDropEffects.None);
        }
    }

    private void FileTransferDragOver(object sender, DragEventArgs e)
    {
        if (TryHandleShelfDrag(e, drop: false)) return;
        var (destination, effect) = ResolveDrop(e);
        _dragFeedbackDestination = effect == DragDropEffects.None ? null : destination;
        e.Effects = effect;
        e.Handled = true;
        ClearDropFeedback();
        _fileDragFeedback?.Show(effect, _dragFeedbackDestination);
    }

    private void FileTransferDrop(object sender, DragEventArgs e)
    {
        if (TryHandleShelfDrag(e, drop: true)) return;
        var (destination, effect) = ResolveDrop(e);
        var paths = _dragPaths;
        ResetDrop();
        e.Handled = true;
        e.Effects = DragDropEffects.None;
        if (destination == null || effect == DragDropEffects.None)
            return;

        try
        {
            var move = effect == DragDropEffects.Move;
            if (!PerformFileTransfer(paths, destination, move)) return;
            if (move) WindowsFileTransfer.ReportMove(e.Data, paste: false);
            else e.Effects = DragDropEffects.Copy;
        }
        catch (Exception ex)
        {
            NotifyFileTransfer($"Aktarım tamamlanamadı: {ex.Message}");
        }
    }

    private bool PerformFileTransfer(string[] paths, string destination, bool move)
    {
        if (_measurementRun != null || _transferInProgress) return false;
        _transferInProgress = true;
        try
        {
            var completed = WindowsFileTransfer.Transfer(paths, destination, move, new WindowInteropHelper(this).Handle);
            NotifyFileTransfer(completed
                ? move ? $"{paths.Length} öğe taşındı." : $"{paths.Length} öğe kopyalandı."
                : "Aktarım tamamlanmadı. Hedef klasörü kontrol et.");
            return completed;
        }
        finally
        {
            _transferInProgress = false;
            if (!_isPreparedForShutdown) _ = ProcessFileChangeAsync();
        }
    }

    private void ShowFileTransferLabel(string text, Point position)
    {
        _transferFeedbackTimer.Stop();
        FileTransferLabel.Visibility = Visibility.Visible;
        FileTransferText.Text = text;
        PositionFileTransferLabel(position);
    }

    private void PositionFileTransferLabel(Point position)
    {
        FileTransferLabel.Measure(new System.Windows.Size(Math.Max(0, FileTransferOverlay.ActualWidth - 16), double.PositiveInfinity));
        Canvas.SetLeft(FileTransferLabel, Math.Clamp(position.X + 14, 8,
            Math.Max(8, FileTransferOverlay.ActualWidth - FileTransferLabel.DesiredSize.Width - 8)));
        Canvas.SetTop(FileTransferLabel, Math.Clamp(position.Y + 18, 4,
            Math.Max(4, FileTransferOverlay.ActualHeight - FileTransferLabel.DesiredSize.Height - 8)));
    }

    private void ClearDropFeedback()
    {
        if (!_transferFeedbackTimer.IsEnabled)
            FileTransferLabel.Visibility = Visibility.Collapsed;
    }

    private void ResetDrop()
    {
        _dragData = null;
        _dragPaths = [];
        _dragFeedbackDestination = null;
        _fileDragFeedback?.Hide();
        ClearDropFeedback();
    }

    private void NotifyFileTransfer(string text)
    {
        ShowFeedback(text);
        ShowFileTransferLabel(text, new Point(0, FileTransferOverlay.ActualHeight));
        _transferFeedbackTimer.Start();
    }

    private void SetCutPaths(string[] paths)
    {
        _cutPaths.Clear();
        _cutPaths.UnionWith(paths);
        foreach (var item in _desktopIcons) item.IsCut = _cutPaths.Contains(item.FullPath);
        foreach (var item in _searchResults) item.IsCut = _cutPaths.Contains(item.FullPath);
    }

    private void ClearClipboardIfUnchanged(uint sequence)
    {
        if (!OpenClipboard(new WindowInteropHelper(this).Handle)) return;
        try
        {
            if (GetClipboardSequenceNumber() == sequence) EmptyClipboard();
        }
        finally { CloseClipboard(); }
    }

    private void StartFileClipboardListener()
    {
        if (_measurementRun != null || PresentationSource.FromVisual(this) is not HwndSource source ||
            !AddClipboardFormatListener(source.Handle))
            return;
        _fileClipboardSource = source;
        source.AddHook(FileClipboardChanged);
    }

    private void StopFileClipboardListener()
    {
        _transferFeedbackTimer.Stop();
        _fileDragFeedback?.Hide();
        if (_fileClipboardSource is not { } source) return;
        RemoveClipboardFormatListener(source.Handle);
        source.RemoveHook(FileClipboardChanged);
        _fileClipboardSource = null;
    }

    private IntPtr FileClipboardChanged(IntPtr hwnd, int message, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        const int clipboardUpdate = 0x031D;
        if (message == clipboardUpdate && _cutPaths.Count > 0 && GetClipboardSequenceNumber() != _cutClipboardSequence)
            SetCutPaths([]);
        return IntPtr.Zero;
    }

    [DllImport("user32.dll")]
    private static extern uint GetClipboardSequenceNumber();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool OpenClipboard(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EmptyClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseClipboard();

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AddClipboardFormatListener(IntPtr hwnd);

    [DllImport("user32.dll")]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool RemoveClipboardFormatListener(IntPtr hwnd);
}
