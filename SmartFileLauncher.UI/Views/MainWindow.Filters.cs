using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private readonly FolderViewMemory _folderViews = new();
    private readonly List<MenuItem> _filterMenuRoots = new();
    private readonly List<MenuItem> _sortMenuRoots = new();
    private readonly List<MenuItem> _filterClearItems = new();
    private readonly List<MenuItem> _relevanceSortItems = new();
    private readonly ObservableCollection<FilterChipViewModel> _filterChips = new();
    private readonly DispatcherTimer _badgePopupClose = new()
    {
        Interval = TimeSpan.FromMilliseconds(280)
    };

    private ResultView _searchView = ResultView.SearchDefault;
    private IReadOnlyList<FolderEntry> _currentFolderEntries = Array.Empty<FolderEntry>();

    private bool IsSearchSurfaceActive => ResultsContainer.Visibility == Visibility.Visible;

    private ResultView ActiveView => IsSearchSurfaceActive
        ? _searchView
        : _folderViews.Get(_currentFolderPath);

    private ItemFilter ActiveFilter => ActiveView.Filter;

    private void InitializeFilters()
    {
        AttachViewMenus(FindResource("FileContextMenu") as ContextMenu);
        AttachViewMenus(FindResource("EmptyAreaContextMenu") as ContextMenu);

        var resultsMenu = new ContextMenu
        {
            Style = FindResource("ModernContextMenu") as Style
        };
        AttachViewMenus(resultsMenu);
        ResultsContainer.ContextMenu = resultsMenu;

        FilterChips.ItemsSource = _filterChips;
        _badgePopupClose.Tick += (_, _) =>
        {
            _badgePopupClose.Stop();
            CloseBadgePopup();
        };
        Deactivated += (_, _) => CloseBadgePopup();

        UpdateFilterBadge();
    }

    private void AttachViewMenus(ContextMenu? menu)
    {
        if (menu == null) return;

        if (menu.Items.Count > 0)
        {
            menu.Items.Add(new Separator { Style = FindResource("MenuSeparator") as Style });
        }

        menu.Items.Add(BuildFilterMenu());
        menu.Items.Add(BuildSortMenu());
    }

    private MenuItem BuildFilterMenu()
    {
        var root = new MenuItem
        {
            Header = "Filtrele",
            Style = FindResource("FilterSubmenuHeader") as Style
        };
        root.SubmenuOpened += (_, _) => SyncViewMenus();

        root.Items.Add(BuildOptionGroup(
            "Tür",
            Options(FileFilterCategories.All, ItemFilterLabels.Category),
            FilterCategory_Click));
        root.Items.Add(BuildOptionGroup(
            "Tarih",
            Options(
                [
                    FilterDateRange.Today,
                    FilterDateRange.LastWeek,
                    FilterDateRange.LastMonth,
                    FilterDateRange.LastYear
                ],
                ItemFilterLabels.Date),
            FilterDate_Click));
        root.Items.Add(BuildOptionGroup(
            "Boyut",
            Options(
                [
                    FilterSizeRange.Small,
                    FilterSizeRange.Medium,
                    FilterSizeRange.Large
                ],
                ItemFilterLabels.Size),
            FilterSize_Click));
        root.Items.Add(BuildOptionGroup(
            "Öğe",
            Options(
                [
                    FilterItemKind.FoldersOnly,
                    FilterItemKind.FilesOnly
                ],
                ItemFilterLabels.Kind),
            FilterKind_Click));

        root.Items.Add(new Separator { Style = FindResource("MenuSeparator") as Style });

        var clear = new MenuItem
        {
            Header = "Filtreleri temizle",
            Tag = "🧹",
            Style = FindResource("ModernMenuItem") as Style
        };
        clear.Click += FilterClear_Click;
        root.Items.Add(clear);
        _filterClearItems.Add(clear);

        _filterMenuRoots.Add(root);
        return root;
    }

    private MenuItem BuildSortMenu()
    {
        var root = new MenuItem
        {
            Header = "Sırala",
            Style = FindResource("SortSubmenuHeader") as Style
        };
        root.SubmenuOpened += (_, _) => SyncViewMenus();

        foreach (var field in new[]
                 {
                     SortField.Relevance,
                     SortField.Name,
                     SortField.Modified,
                     SortField.Size,
                     SortField.Kind
                 })
        {
            var item = new MenuItem
            {
                Header = ItemFilterLabels.SortField(field),
                Tag = field,
                StaysOpenOnClick = true,
                Style = FindResource("ModernCheckMenuItem") as Style
            };
            item.Click += SortField_Click;
            root.Items.Add(item);
            if (field == SortField.Relevance)
            {
                _relevanceSortItems.Add(item);
            }
        }

        root.Items.Add(new Separator { Style = FindResource("MenuSeparator") as Style });

        var descending = new MenuItem
        {
            Header = "Azalan sırala",
            Tag = "descending",
            StaysOpenOnClick = true,
            Style = FindResource("ModernCheckMenuItem") as Style
        };
        descending.Click += SortDirection_Click;
        root.Items.Add(descending);

        _sortMenuRoots.Add(root);
        return root;
    }

    private MenuItem BuildOptionGroup(
        string header,
        IEnumerable<(object Value, string Label)> options,
        RoutedEventHandler handler)
    {
        var group = new MenuItem
        {
            Header = header,
            Style = FindResource("ModernSubmenuHeader") as Style
        };

        foreach (var (value, label) in options)
        {
            var item = new MenuItem
            {
                Header = label,
                Tag = value,
                StaysOpenOnClick = true,
                Style = FindResource("ModernCheckMenuItem") as Style
            };
            item.Click += handler;
            group.Items.Add(item);
        }

        return group;
    }

    private static IEnumerable<(object Value, string Label)> Options<T>(
        IEnumerable<T> values,
        Func<T, string> label)
        where T : notnull =>
        values.Select(value => ((object)value, label(value)));

    private void SyncViewMenus()
    {
        var view = ActiveView;
        var searchSurface = IsSearchSurfaceActive;

        foreach (var root in _filterMenuRoots)
        {
            foreach (var groupItem in root.Items.OfType<MenuItem>())
            {
                foreach (var option in groupItem.Items.OfType<MenuItem>())
                {
                    option.IsChecked = option.Tag switch
                    {
                        FileFilterCategory category => (view.Filter.Categories & category) != 0,
                        FilterDateRange date => view.Filter.Date == date,
                        FilterSizeRange size => view.Filter.Size == size,
                        FilterItemKind kind => view.Filter.Kind == kind,
                        _ => false
                    };
                }
            }
        }

        foreach (var clear in _filterClearItems)
        {
            clear.IsEnabled = view.Filter.IsActive;
        }

        foreach (var root in _sortMenuRoots)
        {
            foreach (var option in root.Items.OfType<MenuItem>())
            {
                option.IsChecked = option.Tag switch
                {
                    SortField field => view.Sort.Field == field,
                    "descending" => view.Sort.Descending,
                    _ => false
                };
            }
        }

        foreach (var item in _relevanceSortItems)
        {
            item.Visibility = searchSurface ? Visibility.Visible : Visibility.Collapsed;
        }
    }

    private void FilterCategory_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: FileFilterCategory category })
        {
            ApplyFilterChange(ActiveFilter.WithCategoryToggled(category));
        }
    }

    private void FilterDate_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: FilterDateRange date })
        {
            var current = ActiveFilter;
            ApplyFilterChange(current with
            {
                Date = current.Date == date ? FilterDateRange.Any : date
            });
        }
    }

    private void FilterSize_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: FilterSizeRange size })
        {
            var current = ActiveFilter;
            ApplyFilterChange(current with
            {
                Size = current.Size == size ? FilterSizeRange.Any : size
            });
        }
    }

    private void FilterKind_Click(object sender, RoutedEventArgs e)
    {
        if (sender is MenuItem { Tag: FilterItemKind kind })
        {
            var current = ActiveFilter;
            ApplyFilterChange(current with
            {
                Kind = current.Kind == kind ? FilterItemKind.Any : kind
            });
        }
    }

    private void FilterClear_Click(object sender, RoutedEventArgs e)
    {
        ApplyFilterChange(ItemFilter.None);
    }

    private void SortField_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not MenuItem { Tag: SortField field }) return;

        var current = ActiveView.Sort;
        var next = current.Field == field
            ? current with { Descending = !current.Descending }
            : new ItemSort(field, ItemSort.DefaultDescending(field));
        ApplySortChange(next);
    }

    private void SortDirection_Click(object sender, RoutedEventArgs e)
    {
        var current = ActiveView.Sort;
        ApplySortChange(current with { Descending = !current.Descending });
    }

    private void ApplyFilterChange(ItemFilter filter)
    {
        ApplyViewChange(ActiveView with { Filter = filter });
    }

    private void ApplySortChange(ItemSort sort)
    {
        ApplyViewChange(ActiveView with { Sort = sort });
    }

    private void ApplyViewChange(ResultView view)
    {
        if (IsSearchSurfaceActive)
        {
            if (_searchView == view) return;

            _searchView = view;
            Log($"🔎 Arama görünümü: {DescribeView(view)}");
            UpdateFilterBadge();
            SyncViewMenus();
            if (!string.IsNullOrWhiteSpace(SearchBox.Text))
            {
                BeginSearch(SearchBox.Text, debounce: false);
            }

            return;
        }

        if (_folderViews.Get(_currentFolderPath) == view) return;

        _folderViews.Set(_currentFolderPath, view);
        Log($"🔎 Klasör görünümü: {DescribeView(view)}");
        UpdateFilterBadge();
        SyncViewMenus();
        RenderFolderEntries();
    }

    private static string DescribeView(ResultView view)
    {
        var parts = ItemFilterLabels.Parts(view.Filter);
        var filter = parts.Count == 0 ? "filtre yok" : string.Join(", ", parts);
        var direction = view.Sort.Descending ? "azalan" : "artan";
        return $"{filter}; sıralama {ItemFilterLabels.SortField(view.Sort.Field)} ({direction})";
    }

    private void UpdateFilterBadge()
    {
        var chips = ItemFilterLabels.Chips(ActiveFilter);
        if (chips.Count == 0)
        {
            _filterChips.Clear();
            FilterBadge.Visibility = Visibility.Collapsed;
            FilterBadge.ToolTip = null;
            CloseBadgePopup();
            return;
        }

        FilterBadgeText.Text = ItemFilterLabels.Badge(ActiveFilter);
        FilterBadge.ToolTip = chips.Count == 1
            ? ItemFilterLabels.Tooltip(ActiveFilter)
            : null;
        FilterBadge.Visibility = Visibility.Visible;

        _filterChips.Clear();
        foreach (var (value, label) in chips)
        {
            _filterChips.Add(new FilterChipViewModel(value, label));
        }

        if (chips.Count < 2)
        {
            CloseBadgePopup();
        }
    }

    private void FilterBadge_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _badgePopupClose.Stop();
        if (_filterChips.Count > 1)
        {
            FilterBadgePopup.IsOpen = true;
        }
    }

    private void FilterBadge_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _badgePopupClose.Start();
    }

    private void FilterBadgePopup_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _badgePopupClose.Stop();
    }

    private void FilterBadgePopup_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e)
    {
        _badgePopupClose.Start();
    }

    private void FilterBadge_MouseLeftButtonDown(object sender, System.Windows.Input.MouseButtonEventArgs e)
    {
        e.Handled = true;
        if (_filterChips.Count < 2) return;

        if (FilterBadgePopup.IsOpen)
        {
            CloseBadgePopup();
            return;
        }

        _badgePopupClose.Stop();
        FilterBadgePopup.IsOpen = true;
    }

    private void FilterBadgePopup_Closed(object sender, EventArgs e)
    {
        _badgePopupClose.Stop();
    }

    private void FilterBadgeClear_Click(object sender, RoutedEventArgs e)
    {
        CloseBadgePopup();
        ApplyFilterChange(ItemFilter.None);
    }

    private void FilterChipRemove_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { DataContext: FilterChipViewModel chip })
        {
            ApplyFilterChange(ItemFilterLabels.Without(ActiveFilter, chip.Value));
        }
    }

    private void CloseBadgePopup()
    {
        _badgePopupClose.Stop();
        FilterBadgePopup.IsOpen = false;
    }

    private List<DesktopIconViewModel> BuildFolderItems(
        IReadOnlyList<FolderEntry> entries,
        ResultView view)
    {
        var now = DateTime.Now;
        var matched = new List<FolderEntry>(entries.Count);
        foreach (var entry in entries)
        {
            if (view.Filter.Matches(
                    entry.Name,
                    entry.IsDirectory,
                    entry.SizeBytes,
                    entry.LastWriteTime,
                    now))
            {
                matched.Add(entry);
            }
        }

        matched.Sort((left, right) => CompareEntries(left, right, view.Sort));

        var items = new List<DesktopIconViewModel>(matched.Count);
        foreach (var entry in matched)
        {
            var viewModel = new DesktopIconViewModel
            {
                Name = entry.Name,
                FullPath = entry.FullPath,
                Icon = entry.IsDirectory ? "folder" : GetFileIcon(entry.Name),
                IsDirectory = entry.IsDirectory
            };

            if (entry.IsDirectory)
            {
                viewModel.SetFolderColors(entry.Name);
            }

            items.Add(viewModel);
        }

        return items;
    }

    private static FolderEntry CreateRootEntry(FileSystemNode node)
    {
        var size = node.Metadata?.SizeBytes;
        var modified = node.Metadata?.LastWriteTime;
        if (size == null || modified == null)
        {
            try
            {
                if (node.IsDirectory)
                {
                    var directory = new DirectoryInfo(node.FullPath);
                    if (directory.Exists) modified ??= directory.LastWriteTime;
                }
                else
                {
                    var file = new FileInfo(node.FullPath);
                    if (file.Exists)
                    {
                        size ??= file.Length;
                        modified ??= file.LastWriteTime;
                    }
                }
            }
            catch (Exception)
            {
            }
        }

        return new FolderEntry(node.Name, node.FullPath, node.IsDirectory, size, modified);
    }

    private static List<FolderEntry> SortEntries(
        IReadOnlyList<FolderEntry> entries,
        ItemSort sort)
    {
        var sorted = new List<FolderEntry>(entries);
        sorted.Sort((left, right) => CompareEntries(left, right, sort));
        return sorted;
    }

    private static int CompareEntries(FolderEntry left, FolderEntry right, ItemSort sort)
    {
        if (left.IsDirectory != right.IsDirectory)
        {
            return left.IsDirectory ? -1 : 1;
        }

        return sort.Compare(Row(left), Row(right));
    }

    private static SortRow Row(FolderEntry entry) => new(
        entry.Name,
        entry.FullPath,
        entry.IsDirectory,
        entry.SizeBytes,
        entry.LastWriteTime,
        0);

    private void RenderFolderEntries()
    {
        var view = _folderViews.Get(_currentFolderPath);
        var items = BuildFolderItems(_currentFolderEntries, view);

        _thumbnailViewport?.Cancel();
        _desktopIcons.Clear();
        RetargetThumbnailViewport(items);
        foreach (var item in items)
        {
            _desktopIcons.Add(item);
        }

        UpdateEmptyFolderState(
            _currentFolderPath,
            items.Count,
            _currentFolderEntries.Count,
            view.Filter);
        RebuildDesktopRows();
        AnimateFolderSwap(items.Count);
    }

    private void UpdateEmptyFolderState(
        string? folderPath,
        int visibleCount,
        int loadedCount,
        ItemFilter filter)
    {
        if (visibleCount > 0)
        {
            EmptyFolderPanel.Visibility = Visibility.Collapsed;
            return;
        }

        if (loadedCount > 0 && filter.IsActive)
        {
            EmptyFolderTitle.Text = "Filtre bu klasörde eşleşme bulmadı";
        }
        else
        {
            var folderName = string.IsNullOrEmpty(folderPath)
                ? string.Empty
                : Path.GetFileName(folderPath);
            if (string.IsNullOrEmpty(folderName)) folderName = folderPath ?? string.Empty;
            EmptyFolderTitle.Text = string.IsNullOrEmpty(folderName)
                ? "Bu konum boş"
                : $"'{folderName}' klasörü boş";
        }

        EmptyFolderPanel.Visibility = Visibility.Visible;
    }
}
