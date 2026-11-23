using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using Button = System.Windows.Controls.Button;
using ListBox = System.Windows.Controls.ListBox;
using ListViewItem = System.Windows.Controls.ListViewItem;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    internal const int SearchGridColumns = 4;
    private readonly ObservableCollection<SearchResultRowViewModel> _searchRows = [];
    private bool _searchRowsSyncQueued;

    private void InitializeSearchGrid()
    {
        ResultsGrid.ItemsSource = _searchRows;
        ResultsList.PreviewKeyDown += FileSearchResultsPreviewKeyDown;
        ResultsGrid.PreviewKeyDown += FileSearchResultsPreviewKeyDown;
        _searchResults.CollectionChanged += (_, _) => QueueSearchRowsSync();
    }

    private void QueueSearchRowsSync()
    {
        if (_searchRowsSyncQueued || _isPreparedForShutdown) return;
        _searchRowsSyncQueued = true;
        _ = Dispatcher.InvokeAsync(SyncSearchRows, DispatcherPriority.DataBind);
    }

    private void SyncSearchRows()
    {
        _searchRowsSyncQueued = false;
        if (_isPreparedForShutdown) return;
        SearchResultRowViewModel.Sync(_searchResults, SearchGridColumns, _searchRows);
        SearchResultLocations.Apply(_searchResults);
    }

    private void SelectFileSearchResult(int index)
    {
        if (ResultsList.SelectedItems.Count > 1) ResultsList.UnselectAll();
        ResultsList.SelectedIndex = index;
        _selectionAnchor = ResultsList.SelectedItem;
        ScrollFileSearchResultIntoView(index);
    }

    private void ScrollFileSearchResultIntoView(int index)
    {
        if (SmoothFileSearchScroll(index)) return;
        if (!_isGridViewMode)
            ResultsList.ScrollIntoView(ResultsList.SelectedItem);
        else if (index / SearchGridColumns < _searchRows.Count)
            ResultsGrid.ScrollIntoView(_searchRows[index / SearchGridColumns]);
    }

    private bool SmoothFileSearchScroll(int index)
    {
        if (!Animate || _searchRowsSyncQueued || SearchResultsScroll() is not { } viewer)
            return false;
        var container = _isGridViewMode
            ? ResultsGrid.ItemContainerGenerator.ContainerFromIndex(index / SearchGridColumns)
            : ResultsList.ItemContainerGenerator.ContainerFromIndex(index);
        return container is FrameworkElement item && SmoothScroll.BringIntoView(viewer, item, true);
    }

    private bool MoveFileSearchGridSelection(int index, Key key)
    {
        if (key is not (Key.Left or Key.Right or Key.Up or Key.Down) ||
            key == Key.Up && index < SearchGridColumns) return false;

        var target = NavigationTarget(key, index, SearchGridColumns, _searchResults.Count, 1) ?? index;
        if (target != index) MoveSearchSelection(index, target, ResultsGrid.IsKeyboardFocusWithin);
        return true;
    }

    private string? FocusedFileSearchPath =>
        (ResultsList.IsKeyboardFocusWithin || ResultsGrid.IsKeyboardFocusWithin) &&
        GetFileItem(Keyboard.FocusedElement as DependencyObject).Item is SearchResultViewModel result
            ? result.FullPath : null;

    private bool FocusSelectedFileSearchResult()
    {
        var index = ResultsList.SelectedIndex;
        ListBox host = _isGridViewMode ? ResultsGrid : ResultsList;
        if (index < 0 || !host.IsVisible) return false;

        var target = FileSearchFocusTarget(index);
        if (target == null)
        {
            ScrollFileSearchResultIntoView(index);
            host.UpdateLayout();
            target = FileSearchFocusTarget(index);
        }
        return target?.Focus() == true;
    }

    private bool StepFileSearchFocus(int step)
    {
        var current = FocusedFileSearchPath is { } path ? IndexOfSearchResult(path) : ResultsList.SelectedIndex;
        if (!HasCurrentFileSearch || current < 0 || current + step < 0) return SearchBox.Focus();
        var target = Math.Min(current + step, _searchResults.Count - 1);
        if (target != current)
        {
            MoveSearchSelection(current, target, focusFollows: true);
            return true;
        }
        if (target != ResultsList.SelectedIndex || ResultsList.SelectedItems.Count > 1) SelectFileSearchResult(target);
        FocusSelectedFileSearchResult();
        RevealKeyboardCard(FileSearchFocusTarget(target));
        return true;
    }

    private FrameworkElement? FileSearchFocusTarget(int index)
    {
        if (!_isGridViewMode)
            return ResultsList.ItemContainerGenerator.ContainerFromIndex(index) as ListViewItem;

        if (ResultsGrid.ItemContainerGenerator.ContainerFromIndex(index / SearchGridColumns) is not FrameworkElement row ||
            FindDescendant<ItemsControl>(row) is not { } cards ||
            cards.ItemContainerGenerator.ContainerFromIndex(index % SearchGridColumns) is not ContentPresenter card)
            return null;

        return FindDescendant<Button>(card);
    }

    private void FileSearchResultsPreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (NameEditing) return;
        var modifiers = Keyboard.Modifiers;
        if (e.Key == Key.Tab)
        {
            if (modifiers is ModifierKeys.None or ModifierKeys.Shift)
                e.Handled = StepFileSearchFocus(modifiers == ModifierKeys.Shift ? -1 : 1);
            return;
        }
        if (HasCurrentFileSearch && (modifiers != ModifierKeys.None || e.Key is Key.Home or Key.End or Key.PageUp or Key.PageDown) &&
            HandleSelectionKey(search: true, e.Key, modifiers, focusOnItems: true))
        {
            e.Handled = true;
            return;
        }
        if (modifiers != ModifierKeys.None || !HasCurrentFileSearch) return;

        if (e.Key == Key.Enter)
        {
            if (FocusedFileSearchPath is { } path) OpenFromKeyboard(path);
            e.Handled = true;
        }
        else if (_isGridViewMode && ResultsList.SelectedIndex is var index && index >= 0)
        {
            if (MoveFileSearchGridSelection(index, e.Key))
            {
                e.Handled = true;
            }
            else if (e.Key == Key.Up)
            {
                if (!HandleApplicationKey(e)) ReturnToSearchBox();
                e.Handled = true;
            }
        }
        else if (!_isGridViewMode && e.Key == Key.Up && ResultsList.SelectedIndex == 0 &&
                 StartMenuHost.Visibility != Visibility.Visible)
        {
            ReturnToSearchBox();
            e.Handled = true;
        }
        else if (!_isGridViewMode && e.Key is Key.Up or Key.Down && ResultsList.SelectedIndex >= 0 &&
                 !(e.Key == Key.Up && ResultsList.SelectedIndex == 0))
        {
            var current = ResultsList.SelectedIndex;
            var target = Math.Clamp(current + (e.Key == Key.Down ? 1 : -1), 0, _searchResults.Count - 1);
            if (target != current) MoveSearchSelection(current, target, focusFollows: true);
            e.Handled = true;
        }
    }

    private void ClearFileSearchSelection()
    {
        ResetKeyboardCardEffects();
        ResultsList.UnselectAll();
        _selectionAnchor = null;
    }

    private void ReturnToSearchBox()
    {
        ClearFileSearchSelection();
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }
}
