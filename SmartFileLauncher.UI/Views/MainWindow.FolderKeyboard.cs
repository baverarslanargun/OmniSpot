using System.Windows;
using System.Windows.Input;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private void InitializeFolderKeyboard() => DesktopIcons.PreviewKeyDown += FolderItemsPreviewKeyDown;

    private bool FolderItemsShown => !SearchSurfaceVisible && !BrowsingApplications &&
        string.IsNullOrWhiteSpace(SearchBox.Text) &&
        DesktopIconsScroll.Visibility == Visibility.Visible && _desktopIcons.Count > 0;

    private int FolderSelectionIndex =>
        _selectionAnchor is DesktopIconViewModel { IsTransferSelected: true } anchor ? _desktopIcons.IndexOf(anchor)
        : _selectedDesktopItems.Count == 1 ? _desktopIcons.IndexOf(_selectedDesktopItems.First())
        : -1;

    private bool FocusFolderItemsFromSearchBox()
    {
        var index = Math.Max(_selectionAnchor is DesktopIconViewModel anchor ? _desktopIcons.IndexOf(anchor) : -1, 0);
        if (!FocusFolderItem(index)) return false;
        RevealKeyboardCard(RealizedFolderCard(index));
        return true;
    }

    private void FolderItemsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var modifiers = Keyboard.Modifiers;
        if (NameEditing || GetFileItem(e.OriginalSource as DependencyObject).Item is not DesktopIconViewModel item) return;
        if (e.Key == Key.Tab && modifiers is ModifierKeys.None or ModifierKeys.Shift)
        {
            e.Handled = StepFolderFocus(item, modifiers == ModifierKeys.Shift ? -1 : 1);
            return;
        }
        if ((modifiers != ModifierKeys.None || e.Key is Key.Home or Key.End or Key.PageUp or Key.PageDown) &&
            HandleSelectionKey(search: false, e.Key, modifiers, focusOnItems: true))
        {
            e.Handled = true;
            return;
        }
        if (modifiers != ModifierKeys.None || e.Key is not (Key.Up or Key.Down or Key.Left or Key.Right)) return;

        var index = _desktopIcons.IndexOf(item);
        if (index < 0) return;
        if (e.Key == Key.Up && index < DesktopColumns)
        {
            ClearFolderSelection();
            SearchBox.Focus();
        }
        else if (FolderArrowTarget(index, e.Key) is var target && target != index)
        {
            MoveFolderSelection(index, target, focusFollows: true);
        }
        e.Handled = true;
    }

    private bool MoveFolderSelectionFromSearchBox(Key key)
    {
        if (!FolderItemsShown || Keyboard.Modifiers != ModifierKeys.None ||
            key is not (Key.Up or Key.Down or Key.Left or Key.Right)) return false;
        var index = FolderSelectionIndex;
        if (index < 0)
        {
            if (key != Key.Down) return false;
            MoveFolderSelection(-1, 0, focusFollows: false);
            return true;
        }
        if (key == Key.Up && index < DesktopColumns)
        {
            ClearFolderSelection();
            return true;
        }
        var target = FolderArrowTarget(index, key);
        if (target != index) MoveFolderSelection(index, target, focusFollows: false);
        return true;
    }

    private bool OpenSelectedFolderItem()
    {
        if (!FolderItemsShown || FolderSelectionIndex is not (>= 0 and var index)) return false;
        OpenFile(_desktopIcons[index].FullPath);
        return true;
    }

    private int FolderArrowTarget(int index, Key key) =>
        NavigationTarget(key, index, DesktopColumns, _desktopIcons.Count, 1) ?? index;

    private void SelectFolderItem(int index)
    {
        SelectFileItem(_desktopIcons[index], ModifierKeys.None);
        ScrollFolderItemIntoView(index);
    }

    private void ScrollFolderItemIntoView(int index)
    {
        if (index < 0 || index >= _desktopIcons.Count) return;
        if (Animate && !_desktopRowsRebuildQueued && DesktopScroll is { } viewer &&
            DesktopIcons.ItemContainerGenerator.ContainerFromIndex(index / DesktopColumns) is FrameworkElement row &&
            SmoothScroll.BringIntoView(viewer, row, true)) return;
        _ = MainItemContainer(_desktopIcons[index].FullPath);
    }

    private void ClearFolderSelection()
    {
        ResetKeyboardCardEffects();
        foreach (var selected in _selectedDesktopItems.ToArray()) SetTransferSelection(selected, false);
        _selectionAnchor = null;
    }

    private void OpenFromKeyboard(string path)
    {
        if (_fileOperations.GetItemKind(path) == FileItemKind.Directory) SearchBox.Focus();
        OpenFile(path);
    }

    private bool StepFolderFocus(DesktopIconViewModel item, int step)
    {
        var index = _desktopIcons.IndexOf(item);
        if (index < 0) return false;
        var target = index + step;
        if (target < 0) return SearchBox.Focus();
        target = Math.Min(target, _desktopIcons.Count - 1);
        if (target == index) return FocusFolderItem(target);
        MoveFolderSelection(index, target, focusFollows: true);
        return true;
    }

    private bool FocusFolderItem(int index)
    {
        if (index < 0 || index >= _desktopIcons.Count) return false;
        var item = _desktopIcons[index];
        if (MainItemContainer(item.FullPath) is not { } target) return false;
        SelectFileItem(item, ModifierKeys.None);
        target.Focus();
        return true;
    }
}
