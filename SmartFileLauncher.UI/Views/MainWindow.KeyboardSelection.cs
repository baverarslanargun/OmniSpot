using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private object? _keyboardCursor;

    internal static int? NavigationTarget(Key key, int index, int columns, int count, int pageRows)
    {
        if (count <= 0 || index < 0 || index >= count || columns <= 0) return null;
        var last = count - 1;
        var column = index % columns;
        var page = Math.Max(1, pageRows) * columns;
        var lastRowSameColumn = Math.Min(last - last % columns + column, last);
        return key switch
        {
            Key.Left => column > 0 ? index - 1 : index,
            Key.Right => column < columns - 1 && index < last ? index + 1 : index,
            Key.Up => index >= columns ? index - columns : index,
            Key.Down => index - column + columns <= last ? Math.Min(index + columns, last) : index,
            Key.Home => 0,
            Key.End => last,
            Key.PageUp => index - page >= 0 ? index - page : column,
            Key.PageDown => index + page <= last ? index + page : Math.Max(index, lastRowSameColumn),
            _ => null
        };
    }

    private bool HandleSelectionKey(bool search, Key key, ModifierKeys modifiers, bool focusOnItems)
    {
        if ((modifiers & ~(ModifierKeys.Shift | ModifierKeys.Control)) != 0) return false;
        var count = search ? _searchResults.Count : _desktopIcons.Count;
        if (count == 0) return false;
        var shift = modifiers.HasFlag(ModifierKeys.Shift);
        var control = modifiers.HasFlag(ModifierKeys.Control);
        var cursor = KeyboardCursorIndex(search);

        if (key == Key.Space && control && !shift)
        {
            if (!focusOnItems || cursor < 0) return false;
            ToggleKeyboardItem(search, cursor);
            return true;
        }

        if (control && !shift && !focusOnItems) return false;
        if (cursor < 0)
        {
            if (key is not (Key.Down or Key.PageDown or Key.Home or Key.End) || control && !shift) return false;
            var first = key == Key.End ? count - 1 : 0;
            if (shift) ExtendKeyboardSelection(search, -1, first, focusOnItems, control);
            else MoveKeyboardSelection(search, -1, first, focusOnItems);
            return true;
        }

        var columns = search ? (_isGridViewMode ? SearchGridColumns : 1) : DesktopColumns;
        if (NavigationTarget(key, cursor, columns, count, VisibleRows(search)) is not { } target) return false;
        if (target == cursor)
        {
            if (!shift && !control && SelectedItemCount > 1) MoveKeyboardSelection(search, cursor, target, focusOnItems);
            return true;
        }
        if (control && !shift) MoveKeyboardCursor(search, cursor, target);
        else if (shift) ExtendKeyboardSelection(search, cursor, target, focusOnItems, control);
        else MoveKeyboardSelection(search, cursor, target, focusOnItems);
        return true;
    }

    private bool HandleSearchBoxSelectionKey(Key key, ModifierKeys modifiers)
    {
        var search = HasCurrentFileSearch;
        if (!search && !FolderItemsShown || !SearchBoxRoutesToItems(search, key, modifiers)) return false;
        var focusOnItems = modifiers.HasFlag(ModifierKeys.Control) && !modifiers.HasFlag(ModifierKeys.Shift);
        return HandleSelectionKey(search, key, modifiers, focusOnItems);
    }

    private bool SearchBoxRoutesToItems(bool search, Key key, ModifierKeys modifiers)
    {
        var working = SelectedItemCount > 0 || SearchBox.Text.Length == 0;
        var grid = (search ? (_isGridViewMode ? SearchGridColumns : 1) : DesktopColumns) > 1;
        var chord = modifiers is ModifierKeys.Shift or ModifierKeys.Control or (ModifierKeys.Control | ModifierKeys.Shift);
        return key switch
        {
            Key.PageUp or Key.PageDown => modifiers is ModifierKeys.None or ModifierKeys.Shift,
            Key.Up or Key.Down => chord,
            Key.Left or Key.Right => working && grid && chord,
            Key.Home or Key.End => working && modifiers is ModifierKeys.None or ModifierKeys.Shift,
            Key.Space => SelectedItemCount > 0 && modifiers == ModifierKeys.Control,
            _ => false
        };
    }

    private int KeyboardCursorIndex(bool search)
    {
        if (search)
        {
            if (FocusedFileSearchPath is { } path && IndexOfSearchResult(path) is >= 0 and var focused) return focused;
            if (_keyboardCursor is SearchResultViewModel cursor && _searchResults.IndexOf(cursor) is >= 0 and var at) return at;
            return HasCurrentFileSearch ? ResultsList.SelectedIndex : -1;
        }
        if (DesktopIcons.IsKeyboardFocusWithin &&
            GetFileItem(Keyboard.FocusedElement as DependencyObject).Item is DesktopIconViewModel item &&
            _desktopIcons.IndexOf(item) is >= 0 and var focusedItem) return focusedItem;
        if (_keyboardCursor is DesktopIconViewModel folderCursor && _desktopIcons.IndexOf(folderCursor) is >= 0 and var index) return index;
        return FolderSelectionIndex;
    }

    private int VisibleRows(bool search)
    {
        var viewer = search ? SearchResultsScroll() : DesktopScroll;
        var row = search
            ? (_isGridViewMode ? ResultsGrid.ItemContainerGenerator.ContainerFromIndex(0) : ResultsList.ItemContainerGenerator.ContainerFromIndex(0))
            : DesktopIcons.ItemContainerGenerator.ContainerFromIndex(0);
        if (viewer is not { ViewportHeight: > 0 } || row is not FrameworkElement { ActualHeight: > 0 } element) return 3;
        return Math.Max(1, (int)(viewer.ViewportHeight / element.ActualHeight));
    }

    private void MoveKeyboardSelection(bool search, int from, int to, bool focusFollows)
    {
        if (search) MoveSearchSelection(from, to, focusFollows);
        else MoveFolderSelection(from, to, focusFollows);
    }

    private void ToggleKeyboardItem(bool search, int index)
    {
        var item = KeyboardItems(search)[index]!;
        SelectFileItem(item, ModifierKeys.Control);
        _keyboardCursor = item;
        if (!search) UpdateSelectionBar();
    }

    private void ExtendKeyboardSelection(bool search, int from, int to, bool focusFollows, bool keepOthers)
    {
        var items = KeyboardItems(search);
        var item = items[to]!;
        if (_selectionAnchor == null || items.IndexOf(_selectionAnchor) < 0)
            _selectionAnchor = from >= 0 ? items[from] : item;
        GlideKeyboard(search, from, to, item, filled: true, focusFollows, () =>
        {
            SelectFileItem(item, keepOthers ? ModifierKeys.Control | ModifierKeys.Shift : ModifierKeys.Shift);
            if (!search) UpdateSelectionBar();
        });
    }

    private void MoveKeyboardCursor(bool search, int from, int to)
    {
        var item = KeyboardItems(search)[to]!;
        GlideKeyboard(search, from, to, item, filled: IsTransferSelected(item), focusFollows: true, change: null);
    }

    private void GlideKeyboard(bool search, int from, int to, object item, bool filled, bool focusFollows, Action? change)
    {
        var source = from >= 0 && from != to ? SelectionHighlight(KeyboardContainer(search, from)) : null;
        var glide = Animate && source != null;
        if (glide) SetArriving(item, true);
        CardNameReveal.SetCurrent(null);
        change?.Invoke();
        _keyboardCursor = item;
        ScrollKeyboardItemIntoView(search, to);
        void Arrive()
        {
            SetArriving(item, false);
            if (!ReferenceEquals(_keyboardCursor, item)) return;
            var container = KeyboardContainer(search, to);
            if (focusFollows) FocusKeyboardItem(search, to);
            RevealKeyboardCard(container);
        }
        if (glide && SelectionHighlight(KeyboardContainer(search, to)) is { } target &&
            Glide.Start(source!, target, Arrive, filled))
            return;
        Arrive();
    }

    private IList KeyboardItems(bool search) => search ? _searchResults : _desktopIcons;

    private FrameworkElement? KeyboardContainer(bool search, int index) =>
        search ? FileSearchFocusTarget(index) : RealizedFolderCard(index);

    private void ScrollKeyboardItemIntoView(bool search, int index)
    {
        if (search) ScrollFileSearchResultIntoView(index);
        else ScrollFolderItemIntoView(index);
    }

    private void FocusKeyboardItem(bool search, int index)
    {
        if (search)
        {
            var target = FileSearchFocusTarget(index);
            if (target == null)
            {
                ScrollFileSearchResultIntoView(index);
                (_isGridViewMode ? (UIElement)ResultsGrid : ResultsList).UpdateLayout();
                target = FileSearchFocusTarget(index);
            }
            target?.Focus();
        }
        else if (index >= 0 && index < _desktopIcons.Count)
        {
            MainItemContainer(_desktopIcons[index].FullPath)?.Focus();
        }
    }

    private static void SetArriving(object item, bool arriving)
    {
        if (item is SearchResultViewModel result) result.SelectionArriving = arriving;
        else if (item is DesktopIconViewModel icon) icon.SelectionArriving = arriving;
    }
}
