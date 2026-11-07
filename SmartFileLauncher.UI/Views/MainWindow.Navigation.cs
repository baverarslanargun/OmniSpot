using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.UI.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListViewItem = System.Windows.Controls.ListViewItem;
using SelectionMode = System.Windows.Controls.SelectionMode;
using TextBoxBase = System.Windows.Controls.Primitives.TextBoxBase;
using ShapePath = System.Windows.Shapes.Path;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private const int NavigationHistoryCapacity = 25;
    private const int FolderPathBudget = 88;
    private static readonly TimeSpan BackspaceNavigationGuard = TimeSpan.FromMilliseconds(400);
    private NavigationHistory<NavigationEntry> _navigationHistory = new(NavigationHistoryCapacity);
    private long _navigationSequence;
    private bool _restoringSearch;
    private long _fileChangeSequence;
    private long _searchBoxEmptiedAt;
    private bool _folderPageTruncated;

    private sealed record NavigationEntry(string? FolderPath, double FolderOffset, SearchSnapshot? Search);

    private sealed record SearchSnapshot(
        string Query,
        bool NaturalLanguage,
        ResultView View,
        SearchRequest? Request,
        SearchOutcome? Outcome,
        SearchResultViewModel[] Results,
        string[] SelectedPaths,
        double Offset,
        string LatencyText,
        object? LatencyToolTip,
        bool LatencyVisible,
        string? Warning,
        long ChangeSequence);

    private sealed record FolderPathTarget(string? Path, string? Highlight);

    private enum NavigationResult
    {
        Completed,
        Superseded,
        Unavailable
    }

    private void InitializeNavigation()
    {
        PreviewKeyDown += NavigationPreviewKeyDown;
        PreviewMouseDown += NavigationPreviewMouseDown;
        SearchBox.TextChanged += (_, _) =>
        {
            if (SearchBox.Text.Length == 0) _searchBoxEmptiedAt = Stopwatch.GetTimestamp();
        };
        DependencyPropertyDescriptor
            .FromProperty(VisibilityProperty, typeof(UIElement))
            .AddValueChanged(DesktopIconsScroll, (_, _) => SyncFolderPathBarVisibility());
        UpdateNavigationButtons();
    }

    private void NavigationPreviewKeyDown(object sender, KeyEventArgs e)
    {
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        var modifiers = Keyboard.Modifiers;
        if (key == Key.BrowserBack || (modifiers == ModifierKeys.Alt && key == Key.Left))
        {
            e.Handled = true;
            _ = NavigateBackAsync();
        }
        else if (key == Key.BrowserForward || (modifiers == ModifierKeys.Alt && key == Key.Right))
        {
            e.Handled = true;
            _ = NavigateForwardAsync();
        }
        else if (modifiers == ModifierKeys.Alt && key == Key.Up && _currentFolderPath != null)
        {
            e.Handled = true;
            _ = NavigateToParentAsync();
        }
        else if (key == Key.Back && modifiers == ModifierKeys.None && !e.IsRepeat && CanNavigateBackWithBackspace())
        {
            e.Handled = true;
            _ = NavigateBackAsync();
        }
    }

    private bool CanNavigateBackWithBackspace() =>
        _navigationHistory.CanGoBack
        && !KeyboardInDualPane
        && SearchBox.Text.Length == 0
        && (Keyboard.FocusedElement is not TextBoxBase || ReferenceEquals(Keyboard.FocusedElement, SearchBox))
        && Stopwatch.GetElapsedTime(_searchBoxEmptiedAt) >= BackspaceNavigationGuard;

    private void NavigationPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton == MouseButton.XButton1)
        {
            e.Handled = true;
            _ = NavigateBackAsync();
        }
        else if (e.ChangedButton == MouseButton.XButton2)
        {
            e.Handled = true;
            _ = NavigateForwardAsync();
        }
    }

    private void BackButton_Click(object sender, RoutedEventArgs e) => _ = NavigateBackAsync();

    private void EmptyFolderUpButton_Click(object sender, RoutedEventArgs e) => _ = NavigateToParentAsync();

    private void ContextMenu_GoToContainingFolder(object sender, RoutedEventArgs e)
    {
        var path = GetPathFromContextMenu(sender);
        var parent = string.IsNullOrEmpty(path) ? null : Path.GetDirectoryName(path.TrimEnd('\\', '/'));
        if (string.IsNullOrEmpty(path) || string.IsNullOrEmpty(parent)) return;
        _ = NavigateToFolderAsync(parent, highlightPath: path);
    }

    private void FileContextMenu_Opened(object sender, RoutedEventArgs e)
    {
        ThumbnailContextMenu_Opened(sender, e);
        if (sender is not ContextMenu menu) return;
        UpdateBatchMenuItems(menu, PathFromMenuTarget(menu));
        UpdateTabMenuItems(menu);
        UpdateDualMenuItems(menu);
        var goToFolder = menu.Items.OfType<MenuItem>().FirstOrDefault(item => item.Name == "GoToContainingFolderMenuItem");
        if (goToFolder == null) return;
        var target = menu.PlacementTarget as FrameworkElement;
        var fromSearch = target?.DataContext is SearchResultViewModel
            || (target as ListViewItem)?.Content is SearchResultViewModel;
        goToFolder.Visibility = fromSearch ? Visibility.Visible : Visibility.Collapsed;
    }

    private async Task NavigateToFolderAsync(string folderPath, string? highlightPath = null)
    {
        var origin = CaptureNavigationEntry();
        var sequence = ++_navigationSequence;
        if (!await ShowFolderAsync(folderPath) || sequence != _navigationSequence) return;
        RememberNavigation(origin);
        if (highlightPath != null) HighlightFolderItem(highlightPath);
    }

    private Task NavigateToParentAsync()
    {
        if (string.IsNullOrEmpty(_currentFolderPath)) return Task.CompletedTask;
        var current = _currentFolderPath;
        var parent = _folderNavigation.GetParentWithinRoots(current, _indexedRootPaths);
        _navigationDirection = -1;
        if (string.IsNullOrEmpty(parent) || _fileOperations.GetItemKind(parent) != FileItemKind.Directory)
        {
            NavigateHome();
            return Task.CompletedTask;
        }

        return NavigateToFolderAsync(parent, highlightPath: current);
    }

    private void NavigateHome()
    {
        var origin = CaptureNavigationEntry();
        ++_navigationSequence;
        _navigationDirection = -1;
        ClearSearchForNavigation();
        ShowHome();
        RememberNavigation(origin);
    }

    private async Task NavigateBackAsync()
    {
        if (!_navigationHistory.TryPeekBack(out var target)) return;
        var current = CaptureNavigationEntry();
        var sequence = ++_navigationSequence;
        _navigationDirection = -1;
        var result = await RestoreNavigationEntryAsync(target, sequence);
        if (result == NavigationResult.Superseded || sequence != _navigationSequence) return;
        if (result == NavigationResult.Completed)
        {
            _navigationHistory.CompleteBack(current);
            ReleaseSnapshotThumbnails(current);
        }
        else
        {
            _navigationHistory.DiscardBack();
            NotifyNavigationUnavailable(target);
        }
        UpdateNavigationButtons();
    }

    private async Task NavigateForwardAsync()
    {
        if (!_navigationHistory.TryPeekForward(out var target)) return;
        var current = CaptureNavigationEntry();
        var sequence = ++_navigationSequence;
        _navigationDirection = 1;
        var result = await RestoreNavigationEntryAsync(target, sequence);
        if (result == NavigationResult.Superseded || sequence != _navigationSequence) return;
        if (result == NavigationResult.Completed)
        {
            _navigationHistory.CompleteForward(current);
            ReleaseSnapshotThumbnails(current);
        }
        else
        {
            _navigationHistory.DiscardForward();
            NotifyNavigationUnavailable(target);
        }
        UpdateNavigationButtons();
    }

    private async Task<NavigationResult> RestoreNavigationEntryAsync(NavigationEntry entry, long sequence)
    {
        if (entry.FolderPath == null)
        {
            if (_currentFolderPath != null) ShowHome();
        }
        else if (!PathsEqual(entry.FolderPath, _currentFolderPath))
        {
            if (_fileOperations.GetItemKind(entry.FolderPath) != FileItemKind.Directory)
                return NavigationResult.Unavailable;
            if (!await ShowFolderAsync(entry.FolderPath))
                return sequence == _navigationSequence ? NavigationResult.Unavailable : NavigationResult.Superseded;
            if (entry.Search == null) RestoreFolderOffset(entry.FolderOffset);
        }

        if (sequence != _navigationSequence) return NavigationResult.Superseded;
        if (entry.Search != null) RestoreSearch(entry.Search);
        else ClearSearchForNavigation();
        return NavigationResult.Completed;
    }

    private async Task<bool> ShowFolderAsync(string folderPath)
    {
        try
        {
            if (_navigationDirection == 0) _navigationDirection = 1;
            Log($"📂 Klasör açılıyor: {folderPath}");

            ShowFolderLoadingIndicator(folderPath);

            try
            {
                if (!await LoadFolderContentsAsync(
                        folderPath,
                        ensureSynchronized: _isIndexed))
                {
                    return false;
                }

                _currentFolderPath = folderPath;
                UpdateFilterBadge();

                SearchBox.Clear();
                ResultsContainer.Visibility = Visibility.Collapsed;
                ShowPanel(DesktopIconsScroll);
                UpdateFolderPathBar();
                return true;
            }
            finally
            {
                HideFolderLoadingIndicator();
            }
        }
        catch (Exception ex)
        {
            Log($"❌ Klasör açılamadı: {ex.Message}");
            ModernDialog.Show(this, "Klasör açılamadı", ex.Message, DialogKind.Warning);
            return false;
        }
    }

    private void ShowHome()
    {
        _currentFolderPath = null;
        LoadDesktopIcons();
        UpdateFilterBadge();
        UpdateFolderPathBar();
    }

    private void ClearSearchForNavigation()
    {
        if (SearchBox.Text.Length > 0) SearchBox.Clear();
        ResultsContainer.Visibility = Visibility.Collapsed;
        ShowPanel(DesktopIconsScroll);
        UpdateFilterBadge();
    }

    private void RememberNavigation(NavigationEntry origin)
    {
        if (origin.Search == null && PathsEqual(origin.FolderPath, _currentFolderPath)) return;
        _navigationHistory.Record(origin);
        ReleaseSnapshotThumbnails(origin);
        UpdateNavigationButtons();
    }

    private NavigationEntry CaptureNavigationEntry() =>
        new(_currentFolderPath, DesktopScroll?.VerticalOffset ?? 0, CaptureSearchSnapshot());

    private SearchSnapshot? CaptureSearchSnapshot()
    {
        var query = SearchBox.Text;
        if (string.IsNullOrWhiteSpace(query) || ResultsContainer.Visibility != Visibility.Visible) return null;
        return new SearchSnapshot(
            query,
            _isNaturalLanguageMode,
            _searchView,
            _completedSearchRequest,
            _completedSearchOutcome,
            _searchResults.ToArray(),
            ResultsList.SelectedItems.OfType<SearchResultViewModel>().Select(result => result.FullPath).ToArray(),
            SearchResultsScroll()?.VerticalOffset ?? 0,
            SearchLatencyText.Text,
            SearchLatencyText.ToolTip,
            SearchLatencyText.Visibility == Visibility.Visible,
            FallbackWarningBanner.Visibility == Visibility.Visible ? FallbackReasonText.Text : null,
            Interlocked.Read(ref _fileChangeSequence));
    }

    private void RestoreSearch(SearchSnapshot snapshot)
    {
        CancelCurrentSearch();
        _restoringSearch = true;
        try
        {
            if (snapshot.NaturalLanguage != _isNaturalLanguageMode && NaturalLanguageToggle.IsEnabled)
                NaturalLanguageToggle.IsChecked = snapshot.NaturalLanguage;
            _searchView = snapshot.View;
            SearchBox.Text = snapshot.Query;
            SearchBox.CaretIndex = snapshot.Query.Length;
        }
        finally
        {
            _restoringSearch = false;
        }

        _lastSearchQuery = snapshot.Query;
        if (snapshot.Request is not { } request ||
            snapshot.Outcome is not { } outcome ||
            request.Query != snapshot.Query)
        {
            BeginSearch(snapshot.Query);
            return;
        }

        Volatile.Write(ref _refreshSearchRequest, request);
        _completedSearchRequest = request;
        _completedSearchOutcome = outcome;
        DesktopIconsScroll.Visibility = Visibility.Collapsed;
        ShowPanel(ResultsContainer);
        SearchingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        CancelSearchThumbnailRequests();
        _searchResults.Clear();
        foreach (var result in snapshot.Results) _searchResults.Add(result);

        var empty = snapshot.Results.Length == 0;
        if (empty) NoResultsHint.Text = NoResultsHintText();
        NoResultsPanel.Visibility = empty ? Visibility.Visible : Visibility.Collapsed;
        ResultsList.Visibility = !empty && !_isGridViewMode ? Visibility.Visible : Visibility.Collapsed;
        ResultsGridScroll.Visibility = !empty && _isGridViewMode ? Visibility.Visible : Visibility.Collapsed;
        FallbackReasonText.Text = snapshot.Warning ?? string.Empty;
        FallbackWarningBanner.Visibility = snapshot.Warning == null ? Visibility.Collapsed : Visibility.Visible;
        SearchLatencyText.Text = snapshot.LatencyText;
        SearchLatencyText.ToolTip = snapshot.LatencyToolTip;
        SearchLatencyText.Visibility = snapshot.LatencyVisible ? Visibility.Visible : Visibility.Collapsed;
        UpdateFilterBadge();
        SyncViewMenus();

        if (ResultsList.SelectionMode != SelectionMode.Single)
        {
            ResultsList.SelectedItems.Clear();
            var selected = snapshot.SelectedPaths.ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var result in snapshot.Results.Where(result => selected.Contains(result.FullPath)))
                ResultsList.SelectedItems.Add(result);
        }

        var offset = snapshot.Offset;
        if (offset > 0)
            _ = Dispatcher.InvokeAsync(() => SearchResultsScroll()?.ScrollToVerticalOffset(offset), DispatcherPriority.Background);
        ReloadSearchThumbnails();
        Log($"↩️ Aramaya dönüldü: '{snapshot.Query}' ({snapshot.Results.Length} sonuç, sorgu yeniden çalışmadı)");

        if (Interlocked.Read(ref _fileChangeSequence) != snapshot.ChangeSequence)
            _ = RefreshSearchResultsAsync();
    }

    private ScrollViewer? SearchResultsScroll() =>
        _isGridViewMode ? ResultsGridScroll : FindDescendant<ScrollViewer>(ResultsList);

    private void RestoreFolderOffset(double offset)
    {
        if (offset <= 0) return;
        _ = Dispatcher.InvokeAsync(() => DesktopScroll?.ScrollToVerticalOffset(offset), DispatcherPriority.Background);
    }

    private static void ReleaseSnapshotThumbnails(NavigationEntry entry)
    {
        if (entry.Search == null) return;
        foreach (var result in entry.Search.Results) result.Thumbnail = null;
    }

    private void HighlightFolderItem(string path)
    {
        var item = _desktopIcons.FirstOrDefault(icon => PathsEqual(icon.FullPath, path));
        if (item == null) return;
        foreach (var selected in _selectedDesktopItems.ToArray()) SetTransferSelection(selected, false);
        SetTransferSelection(item, true);
        _selectionAnchor = item;
        var index = _desktopIcons.IndexOf(item);
        _ = Dispatcher.InvokeAsync(() =>
        {
            var row = index / DesktopColumns;
            if (row >= 0 && row < _desktopRows.Count) DesktopIcons.ScrollIntoView(_desktopRows[row]);
        }, DispatcherPriority.Background);
    }

    private void NotifyNavigationUnavailable(NavigationEntry entry)
    {
        var name = DescribeNavigation(entry);
        Log($"⚠️ Gezinme hedefi artık yok: {name}");
        NotifyFileTransfer($"“{name}” artık bulunamadı; geçmişten çıkarıldı.");
    }

    private void UpdateNavigationButtons()
    {
        BackButton.Visibility = _navigationHistory.CanGoBack ? Visibility.Visible : Visibility.Collapsed;
        var backText = _navigationHistory.TryPeekBack(out var back)
            ? $"Geri: {DescribeNavigation(back)} (Alt+←)"
            : "Geri (Alt+←)";
        BackButton.ToolTip = _navigationHistory.TryPeekForward(out var forward)
            ? $"{backText}\nİleri: {DescribeNavigation(forward)} (Alt+→ veya farenin ileri tuşu)"
            : backText;
        UpdateTabStrip();
        if (DualPaneOpen) UpdateDualPaneFooter();
    }

    private static string DescribeNavigation(NavigationEntry entry) =>
        entry.Search != null ? $"“{entry.Search.Query}” araması"
        : entry.FolderPath == null ? "Ana sayfa"
        : FolderDisplayName(entry.FolderPath);

    private static string FolderDisplayName(string path)
    {
        var trimmed = path.TrimEnd('\\', '/');
        var name = Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static bool PathsEqual(string? left, string? right) =>
        string.Equals(left?.TrimEnd('\\', '/'), right?.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase);

    private void UpdateFolderPathBar()
    {
        FolderPathSegments.Children.Clear();
        SyncFolderPathBarVisibility();
        if (string.IsNullOrEmpty(_currentFolderPath)) return;

        FillPathSegments(FolderPathSegments, _currentFolderPath, FolderPathBudget, parentHint: true, target =>
        {
            _navigationDirection = -1;
            if (target.Path == null) NavigateHome();
            else _ = NavigateToFolderAsync(target.Path, target.Highlight);
        });
        UpdateFolderItemCount();
    }

    private void FillPathSegments(
        System.Windows.Controls.Panel panel,
        string folder,
        int budget,
        bool parentHint,
        Action<FolderPathTarget> navigate)
    {
        var trail = FolderPathTrail.Build(folder, _indexedRootPaths);
        var layout = FolderPathTrail.Fit(trail, budget);
        FolderPathTarget TargetOf(FolderPathSegment segment)
        {
            var index = trail.ToList().FindIndex(item => item.Path == segment.Path);
            return new FolderPathTarget(segment.Path, index >= 0 && index + 1 < trail.Count ? trail[index + 1].Path : null);
        }

        System.Windows.Controls.Button CreateButton(string toolTip, Action click)
        {
            var button = new System.Windows.Controls.Button
            {
                Style = (Style)FindResource("FolderPathSegmentButton"),
                ToolTip = toolTip
            };
            button.Click += (_, _) => click();
            return button;
        }

        var home = CreateButton("Ana sayfa", () => navigate(new FolderPathTarget(null, trail[0].Path)));
        home.Content = CreateHomeGlyph();
        panel.Children.Add(home);

        void AddSegment(FolderPathSegment segment)
        {
            panel.Children.Add(CreateFolderPathSeparator());
            if (segment == trail[^1])
            {
                panel.Children.Add(new Border
                {
                    Padding = new Thickness(6, 3, 6, 3),
                    VerticalAlignment = VerticalAlignment.Center,
                    ToolTip = segment.Path,
                    Child = CreateFolderPathText(segment.Name, FontWeights.SemiBold, "#1E293B")
                });
                return;
            }

            var isParent = parentHint && trail.Count >= 2 && segment == trail[^2];
            var button = CreateButton(isParent ? $"{segment.Path} (Alt+↑)" : segment.Path, () => navigate(TargetOf(segment)));
            button.Content = CreateFolderPathText(segment.Name, FontWeights.Normal, null);
            panel.Children.Add(button);
        }

        foreach (var segment in layout.Leading) AddSegment(segment);
        if (layout.Hidden.Count > 0)
        {
            panel.Children.Add(CreateFolderPathSeparator());
            System.Windows.Controls.Button? overflow = null;
            overflow = CreateButton("Gizlenen klasörler", () => ShowHiddenFolderPath(overflow!, layout.Hidden.Select(TargetOf).ToArray(), navigate));
            overflow.Content = CreateFolderPathText("…", FontWeights.Normal, null);
            panel.Children.Add(overflow);
        }
        foreach (var segment in layout.Trailing) AddSegment(segment);
    }

    private void SyncFolderPathBarVisibility()
    {
        FolderPathBar.Visibility = !string.IsNullOrEmpty(_currentFolderPath) &&
            DesktopIconsScroll.Visibility == Visibility.Visible
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void UpdateFolderItemCount()
    {
        var count = _desktopIcons.Count;
        FolderItemCountText.Text = _folderPageTruncated ? $"{count:N0}+ öğe" : $"{count:N0} öğe";
    }

    private static TextBlock CreateFolderPathText(string text, FontWeight weight, string? foreground)
    {
        var block = new TextBlock
        {
            Text = text,
            FontWeight = weight,
            MaxWidth = 180,
            TextTrimming = TextTrimming.CharacterEllipsis,
            VerticalAlignment = VerticalAlignment.Center
        };
        if (foreground != null)
            block.Foreground = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString(foreground)!;
        return block;
    }

    private static ShapePath CreateFolderPathSeparator() => new()
    {
        Data = System.Windows.Media.Geometry.Parse("M 0,0 L 3.5,3.5 L 0,7"),
        Stroke = (System.Windows.Media.Brush)new System.Windows.Media.BrushConverter().ConvertFromString("#CBD5E1")!,
        StrokeThickness = 1.3,
        StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
        StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
        StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
        Margin = new Thickness(3, 0, 3, 0),
        VerticalAlignment = VerticalAlignment.Center
    };

    private static ShapePath CreateHomeGlyph()
    {
        var glyph = new ShapePath
        {
            Data = System.Windows.Media.Geometry.Parse("M 1.5,6.5 L 7,1.8 L 12.5,6.5 M 3,5.4 V 12 H 11 V 5.4"),
            StrokeThickness = 1.4,
            StrokeStartLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeEndLineCap = System.Windows.Media.PenLineCap.Round,
            StrokeLineJoin = System.Windows.Media.PenLineJoin.Round,
            Width = 14,
            Height = 14,
            VerticalAlignment = VerticalAlignment.Center
        };
        glyph.SetBinding(ShapePath.StrokeProperty, new System.Windows.Data.Binding
        {
            Path = new PropertyPath(System.Windows.Controls.Control.ForegroundProperty),
            RelativeSource = new RelativeSource(RelativeSourceMode.FindAncestor, typeof(System.Windows.Controls.Button), 1)
        });
        return glyph;
    }

    private void ShowHiddenFolderPath(
        FrameworkElement anchor,
        IReadOnlyList<FolderPathTarget> hidden,
        Action<FolderPathTarget> navigate)
    {
        var menu = new ContextMenu
        {
            Style = FindResource("ModernContextMenu") as Style,
            PlacementTarget = anchor,
            Placement = PlacementMode.Bottom
        };
        foreach (var target in hidden)
        {
            var item = new MenuItem
            {
                Header = FolderDisplayName(target.Path!),
                Tag = "📁",
                ToolTip = target.Path,
                Style = FindResource("ModernMenuItem") as Style
            };
            item.Click += (_, _) => navigate(target);
            menu.Items.Add(item);
        }
        menu.IsOpen = true;
    }
}
