using System.Windows;
using System.Windows.Controls;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using Control = System.Windows.Controls.Control;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private SelectionGlide? _selectionGlide;

    private SelectionGlide Glide => _selectionGlide ??= new SelectionGlide(SelectionGlideLayer);

    private void ResetKeyboardCardEffects()
    {
        _selectionGlide?.Finish();
        CardNameReveal.SetCurrent(null);
        _keyboardCursor = null;
    }

    private void MoveSearchSelection(int from, int to, bool focusFollows)
    {
        if (to < 0 || to >= _searchResults.Count) return;
        var result = _searchResults[to];
        var source = from >= 0 && from != to ? SelectionHighlight(FileSearchFocusTarget(from)) : null;
        var glide = Animate && source != null;
        if (glide) result.SelectionArriving = true;
        CardNameReveal.SetCurrent(null);
        SelectFileSearchResult(to);
        _keyboardCursor = result;
        if (glide && SelectionHighlight(FileSearchFocusTarget(to)) is { } target &&
            Glide.Start(source!, target, () => ArriveAtSearchResult(result, focusFollows)))
            return;
        ArriveAtSearchResult(result, focusFollows);
    }

    private void ArriveAtSearchResult(SearchResultViewModel result, bool focusFollows)
    {
        result.SelectionArriving = false;
        if (!ReferenceEquals(ResultsList.SelectedItem, result)) return;
        if (focusFollows) FocusSelectedFileSearchResult();
        RevealKeyboardCard(FileSearchFocusTarget(ResultsList.SelectedIndex));
    }

    private void MoveFolderSelection(int from, int to, bool focusFollows)
    {
        if (to < 0 || to >= _desktopIcons.Count) return;
        var item = _desktopIcons[to];
        var source = from >= 0 && from != to ? SelectionHighlight(RealizedFolderCard(from)) : null;
        var glide = Animate && source != null;
        if (glide) item.SelectionArriving = true;
        CardNameReveal.SetCurrent(null);
        SelectFolderItem(to);
        _keyboardCursor = item;
        if (glide && SelectionHighlight(RealizedFolderCard(to)) is { } target &&
            Glide.Start(source!, target, () => ArriveAtFolderItem(item, focusFollows)))
            return;
        ArriveAtFolderItem(item, focusFollows);
    }

    private void ArriveAtFolderItem(DesktopIconViewModel item, bool focusFollows)
    {
        item.SelectionArriving = false;
        if (!item.IsTransferSelected) return;
        var index = _desktopIcons.IndexOf(item);
        if (index < 0) return;
        if (focusFollows) FocusFolderItem(index);
        RevealKeyboardCard(RealizedFolderCard(index));
    }

    private void RevealKeyboardCard(FrameworkElement? card) =>
        CardNameReveal.SetCurrent(_isGridViewMode ? card : null);

    private static Border? SelectionHighlight(FrameworkElement? item)
    {
        if (item is not Control { Template: { } template } control) return null;
        foreach (var part in new[] { "CardBorder", "Row", "border" })
            if (template.FindName(part, control) is Border border) return border;
        return null;
    }

    private FrameworkElement? RealizedFolderCard(int index)
    {
        if (index < 0 || index >= _desktopIcons.Count || _desktopRowsRebuildQueued) return null;
        return DesktopIcons.ItemContainerGenerator.ContainerFromIndex(index / DesktopColumns) is DependencyObject row
            ? FindItemButton(row, _desktopIcons[index].FullPath)
            : null;
    }
}
