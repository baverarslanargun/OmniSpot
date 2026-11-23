using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.UI.ViewModels;
using Brush = System.Windows.Media.Brush;
using Color = System.Windows.Media.Color;
using ColorConverter = System.Windows.Media.ColorConverter;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private static readonly Brush ScopeChipBackground = Frozen("#FFFFFF");
    private static readonly Brush ScopeChipBorder = Frozen("#D6DEEA");
    private static readonly Brush ScopeChipInk = Frozen("#334155");
    private static readonly Brush ScopeChipIconInk = Frozen("#64748B");
    private static readonly Brush ScopeChipArmedBackground = Frozen("#E4EDFB");
    private static readonly Brush ScopeChipArmedBorder = Frozen("#3F7EE8");
    private static readonly Brush ScopeChipArmedInk = Frozen("#2F62BF");

    private bool _searchScopeDismissed;
    private bool _searchScopeArmed;

    private string? ActiveSearchScope =>
        !_searchScopeDismissed && !string.IsNullOrEmpty(_currentFolderPath) ? _currentFolderPath : null;

    private bool SearchScopeChipShown => SearchScopeChip.Visibility == Visibility.Visible;

    private static Brush Frozen(string color)
    {
        var brush = new SolidColorBrush((Color)ColorConverter.ConvertFromString(color));
        brush.Freeze();
        return brush;
    }

    private void InitializeSearchScope()
    {
        DependencyPropertyDescriptor.FromProperty(VisibilityProperty, typeof(UIElement))
            .AddValueChanged(ResultsContainer, (_, _) => SyncSearchScope());
        SearchBox.TextChanged += (_, _) =>
        {
            if (SearchBox.Text.Length == 0) _searchScopeDismissed = false;
            _searchScopeArmed = false;
            SyncSearchScope();
        };
        SearchBox.LostKeyboardFocus += (_, _) =>
        {
            if (!_searchScopeArmed) return;
            _searchScopeArmed = false;
            SyncSearchScope();
        };
        SearchBarLayout.SizeChanged += (_, e) => FitSearchScopeChip(e.NewSize.Width);
        FitSearchScopeChip(SearchBarLayout.ActualWidth);
        SyncSearchScope();
    }

    private void FitSearchScopeChip(double width)
    {
        if (width <= 0) return;
        SearchScopeChipText.MaxWidth = Math.Clamp(Math.Floor(width * 0.22), 48, 160);
    }

    private void SyncSearchScope()
    {
        var scope = ActiveSearchScope;
        var typed = SearchBox.Text.Length > 0;
        var chip = scope != null && typed && !BrowsingApplications;
        SearchScopeChip.Visibility = chip ? Visibility.Visible : Visibility.Collapsed;
        if (chip)
        {
            SearchScopeChipText.Text = FolderDisplayName(scope!);
            SearchScopeChip.ToolTip = scope;
            SearchScopeChip.Background = _searchScopeArmed ? ScopeChipArmedBackground : ScopeChipBackground;
            SearchScopeChip.BorderBrush = _searchScopeArmed ? ScopeChipArmedBorder : ScopeChipBorder;
            SearchScopeChipText.Foreground = _searchScopeArmed ? ScopeChipArmedInk : ScopeChipInk;
            SearchScopeChipIcon.Stroke = _searchScopeArmed ? ScopeChipArmedInk : ScopeChipIconInk;
        }

        var folder = _currentFolderPath;
        var bar = !string.IsNullOrEmpty(folder) && typed && !BrowsingApplications &&
            ResultsContainer.Visibility == Visibility.Visible;
        SearchScopeBar.Visibility = bar ? Visibility.Visible : Visibility.Collapsed;
        if (bar)
        {
            var name = FolderDisplayName(folder!);
            SearchScopeBarName.Text = scope != null ? name : "Tüm bilgisayarda";
            SearchScopeBarSuffix.Text = scope != null ? " içinde" : "";
            SearchScopeBarIcon.Visibility = scope != null ? Visibility.Visible : Visibility.Collapsed;
            SearchScopeToggleText.Text = scope != null ? "Tüm bilgisayarda ara" : $"Yalnız {name} içinde ara";
            SearchScopeBar.ToolTip = folder;
        }

        NoResultsWidenButton.Visibility = scope != null ? Visibility.Visible : Visibility.Collapsed;
        UpdateSearchHint();
    }

    private string SearchHintText()
    {
        if (ActiveSearchScope is { } scope)
        {
            var name = FolderDisplayName(scope);
            return _isNaturalLanguageMode ? $"🤖 {name} içinde doğal dil ile ara" : $"{name} içinde ara...";
        }
        return _isNaturalLanguageMode ? "🤖 OmniSpot AI - Doğal dil ile ara" : "Uygulama, dosya veya klasör ara...";
    }

    private void UpdateSearchHint()
    {
        if (!BrowsingApplications) SearchWatermark.Text = SearchHintText();
    }

    private string? ScopedNoResultsHint() => ActiveSearchScope is { } scope
        ? _searchView.Filter.IsActive
            ? $"{FolderDisplayName(scope)} içinde etkin filtreyle eşleşen öğe yok"
            : $"{FolderDisplayName(scope)} ve alt klasörlerinde eşleşme yok"
        : null;

    private void SetSearchScopeDismissed(bool dismissed)
    {
        var changed = _searchScopeDismissed != dismissed;
        _searchScopeDismissed = dismissed;
        _searchScopeArmed = false;
        SyncSearchScope();
        if (changed && !string.IsNullOrWhiteSpace(SearchBox.Text))
        {
            Log(dismissed ? "🔎 Arama tüm bilgisayara genişletildi" : $"🔎 Arama {FolderDisplayName(_currentFolderPath ?? "")} içine daraltıldı");
            BeginSearch(SearchBox.Text);
        }
    }

    private bool HandleSearchScopeKey(KeyEventArgs e)
    {
        if (e.Key == Key.Back && Keyboard.Modifiers == ModifierKeys.None && SearchScopeChipShown &&
            SearchBox.CaretIndex == 0 && SearchBox.SelectionLength == 0)
        {
            if (_searchScopeArmed)
            {
                SetSearchScopeDismissed(true);
            }
            else
            {
                _searchScopeArmed = true;
                SyncSearchScope();
            }
            e.Handled = true;
            return true;
        }

        if (_searchScopeArmed && e.Key is not (Key.LeftShift or Key.RightShift or Key.LeftCtrl or Key.RightCtrl or
                Key.LeftAlt or Key.RightAlt or Key.System))
        {
            _searchScopeArmed = false;
            SyncSearchScope();
        }
        return false;
    }

    private bool HandleSearchInFolderShortcut()
    {
        if (BrowsingApplications || KeyboardInDualPane) return false;
        var folder = SelectedFolderForSearch();
        if (folder != null && !PathsEqual(folder, _currentFolderPath))
        {
            _ = SearchInFolderAsync(folder);
            return true;
        }

        SearchBox.Focus();
        SearchBox.SelectAll();
        return true;
    }

    private string? SelectedFolderForSearch()
    {
        if (SearchSurfaceVisible)
            return ResultsList.SelectedItem is SearchResultViewModel { IsDirectory: true } result ? result.FullPath : null;
        var path = _selectedItemPath;
        return !string.IsNullOrEmpty(path) && _fileOperations.GetItemKind(path) == FileItemKind.Directory ? path : null;
    }

    private async Task SearchInFolderAsync(string folder)
    {
        if (!await NavigateToFolderAsync(folder) || !PathsEqual(_currentFolderPath, folder)) return;
        SearchBox.Focus();
        SearchBox.SelectAll();
    }

    private void UpdateSearchInFolderMenuItem(ContextMenu menu)
    {
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(candidate => candidate.Name == "SearchInFolderMenuItem");
        if (item == null) return;
        var path = PathFromMenuTarget(menu);
        item.Visibility = !string.IsNullOrEmpty(path) && !KeyboardInDualPane &&
            _fileOperations.GetItemKind(path) == FileItemKind.Directory
                ? Visibility.Visible
                : Visibility.Collapsed;
    }

    private void ContextMenu_SearchInFolder(object sender, RoutedEventArgs e)
    {
        var path = GetPathFromContextMenu(sender);
        if (string.IsNullOrEmpty(path) || _fileOperations.GetItemKind(path) != FileItemKind.Directory) return;
        _ = SearchInFolderAsync(path);
    }

    private void SearchScopeChipClear_Click(object sender, RoutedEventArgs e)
    {
        SetSearchScopeDismissed(true);
        SearchBox.Focus();
    }

    private void SearchScopeToggle_Click(object sender, RoutedEventArgs e)
    {
        SetSearchScopeDismissed(ActiveSearchScope != null);
        SearchBox.Focus();
    }

    private void SearchEverywhere_Click(object sender, RoutedEventArgs e)
    {
        SetSearchScopeDismissed(true);
        SearchBox.Focus();
    }
}
