using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.UI.ViewModels;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private const int MaxTabs = 12;
    private readonly List<BrowserTab> _tabs = [];
    private int _activeTab;
    private bool _tabSwitchInProgress;

    private sealed class BrowserTab(NavigationHistory<NavigationEntry> history)
    {
        public NavigationHistory<NavigationEntry> History { get; } = history;
        public NavigationEntry? Entry { get; set; }
    }

    private static NavigationEntry HomeEntry => new(null, 0, null);

    private void InitializeTabs()
    {
        _tabs.Add(new BrowserTab(_navigationHistory));
        PreviewKeyDown += TabsPreviewKeyDown;
        FileContentArea.PreviewMouseDown += TabsMiddleClick;
        SearchBox.TextChanged += (_, _) =>
        {
            if (_tabs.Count > 1) UpdateTabStrip();
        };
        UpdateTabStrip();
    }

    private void TabsPreviewKeyDown(object sender, KeyEventArgs e)
    {
        if (Keyboard.Modifiers.HasFlag(ModifierKeys.Alt)) return;
        var control = Keyboard.Modifiers.HasFlag(ModifierKeys.Control);
        var shift = Keyboard.Modifiers.HasFlag(ModifierKeys.Shift);
        if (!control) return;

        if (e.Key == Key.T && !shift)
        {
            e.Handled = true;
            _ = OpenTabAsync(HomeEntry, activate: true);
        }
        else if (e.Key == Key.W && !shift && _tabs.Count > 1)
        {
            e.Handled = true;
            _ = CloseTabAsync(_activeTab);
        }
        else if (e.Key == Key.Tab && _tabs.Count > 1)
        {
            e.Handled = true;
            _ = ActivateTabAsync((_activeTab + (shift ? _tabs.Count - 1 : 1)) % _tabs.Count);
        }
        else if (!shift && e.Key is >= Key.D1 and <= Key.D9 && _tabs.Count > 1)
        {
            e.Handled = true;
            var index = e.Key == Key.D9 ? _tabs.Count - 1 : e.Key - Key.D1;
            if (index < _tabs.Count) _ = ActivateTabAsync(index);
        }
    }

    private void TabsMiddleClick(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Middle) return;
        if (GetFolderPath(GetFileItem(e.OriginalSource as DependencyObject).Item) is not { } folder) return;
        e.Handled = true;
        _ = OpenTabAsync(new NavigationEntry(folder, 0, null), activate: false);
    }

    private void NewTabMenuItem_Click(object sender, RoutedEventArgs e) => _ = OpenTabAsync(HomeEntry, activate: true);

    private void ContextMenu_OpenInNewTab(object sender, RoutedEventArgs e)
    {
        var path = GetPathFromContextMenu(sender);
        if (!string.IsNullOrEmpty(path)) _ = OpenTabAsync(new NavigationEntry(path, 0, null), activate: true);
    }

    private async Task OpenTabAsync(NavigationEntry entry, bool activate)
    {
        if (_tabSwitchInProgress) return;
        if (_tabs.Count >= MaxTabs)
        {
            NotifyFileTransfer($"En çok {MaxTabs} sekme açılabilir.");
            return;
        }

        var index = _activeTab + 1;
        _tabs.Insert(index, new BrowserTab(new NavigationHistory<NavigationEntry>(NavigationHistoryCapacity)) { Entry = entry });
        if (!activate)
        {
            UpdateTabStrip();
            NotifyFileTransfer($"“{TabLabel(entry)}” yeni sekmede açıldı.");
            return;
        }

        await ActivateTabAsync(index);
        if (entry.FolderPath == null) SearchBox.Focus();
    }

    private async Task ActivateTabAsync(int index)
    {
        if (_tabSwitchInProgress || index < 0 || index >= _tabs.Count || index == _activeTab) return;
        _tabSwitchInProgress = true;
        try
        {
            var previous = _tabs[_activeTab];
            previous.Entry = CaptureNavigationEntry();
            var target = _tabs[index];
            _activeTab = index;
            _navigationHistory = target.History;
            var sequence = ++_navigationSequence;
            var result = await RestoreNavigationEntryAsync(target.Entry ?? HomeEntry, sequence);
            if (result == NavigationResult.Unavailable && sequence == _navigationSequence)
            {
                ClearSearchForNavigation();
                ShowHome();
            }

            ReleaseSnapshotThumbnails(previous.Entry);
            target.Entry = null;
            UpdateNavigationButtons();
        }
        finally
        {
            _tabSwitchInProgress = false;
        }
    }

    private async Task CloseTabAsync(int index)
    {
        if (_tabSwitchInProgress || _tabs.Count <= 1 || index < 0 || index >= _tabs.Count) return;
        if (index == _activeTab)
        {
            await ActivateTabAsync(index + 1 < _tabs.Count ? index + 1 : index - 1);
            if (index == _activeTab) return;
        }

        var closing = _tabs[index];
        _tabs.RemoveAt(index);
        if (index < _activeTab) _activeTab--;
        if (closing.Entry != null) ReleaseSnapshotThumbnails(closing.Entry);
        UpdateTabStrip();
    }

    private void UpdateTabStrip()
    {
        if (_tabs.Count <= 1)
        {
            if (TabStrip.Visibility == Visibility.Visible)
            {
                TabStrip.Visibility = Visibility.Collapsed;
                TabStripItems.Children.Clear();
                UpdateHeaderCorners();
            }
            return;
        }

        TabStrip.Visibility = Visibility.Visible;
        UpdateHeaderCorners();
        TabStripItems.Children.Clear();
        var titleWidth = Math.Clamp(560.0 / _tabs.Count - 44, 44, 150);
        for (var index = 0; index < _tabs.Count; index++)
        {
            TabStripItems.Children.Add(CreateTabButton(index, titleWidth));
        }

        var add = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("TabAddButton"),
            ToolTip = "Yeni sekme (Ctrl+T)"
        };
        System.Windows.Automation.AutomationProperties.SetName(add, "Yeni sekme");
        add.Click += NewTabMenuItem_Click;
        TabStripItems.Children.Add(add);
    }

    private System.Windows.Controls.Button CreateTabButton(int index, double titleWidth)
    {
        var active = index == _activeTab;
        var label = active ? CurrentTabLabel() : TabLabel(_tabs[index].Entry ?? HomeEntry);
        var title = new TextBlock
        {
            Text = label,
            MaxWidth = titleWidth,
            TextTrimming = TextTrimming.CharacterEllipsis,
            FontSize = 12.5,
            FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal,
            VerticalAlignment = VerticalAlignment.Center
        };
        var close = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource("TabCloseButton"),
            ToolTip = "Sekmeyi kapat (Ctrl+W)",
            Margin = new Thickness(8, 0, 0, 0)
        };
        System.Windows.Automation.AutomationProperties.SetName(close, $"{label} sekmesini kapat");
        close.Click += (_, e) =>
        {
            e.Handled = true;
            _ = CloseTabAsync(index);
        };

        var panel = new StackPanel { Orientation = System.Windows.Controls.Orientation.Horizontal };
        panel.Children.Add(title);
        panel.Children.Add(close);
        var tab = new System.Windows.Controls.Button
        {
            Style = (Style)FindResource(active ? "BrowserTabActive" : "BrowserTab"),
            Content = panel,
            ToolTip = index < 9 ? $"{label} (Ctrl+{index + 1})" : label
        };
        tab.Click += (_, _) => _ = ActivateTabAsync(index);
        tab.PreviewMouseDown += (_, e) =>
        {
            if (e.ChangedButton != MouseButton.Middle) return;
            e.Handled = true;
            _ = CloseTabAsync(index);
        };
        return tab;
    }

    private string CurrentTabLabel()
    {
        var query = SearchBox.Text.Trim();
        return query.Length > 0 && ResultsContainer.Visibility == Visibility.Visible
            ? $"“{query}”"
            : _currentFolderPath == null ? "Ana sayfa" : FolderDisplayName(_currentFolderPath);
    }

    private static string TabLabel(NavigationEntry entry) =>
        entry.Search != null ? $"“{entry.Search.Query.Trim()}”"
        : entry.FolderPath == null ? "Ana sayfa"
        : FolderDisplayName(entry.FolderPath);

    private void UpdateTabMenuItems(ContextMenu menu)
    {
        var item = menu.Items.OfType<MenuItem>().FirstOrDefault(menuItem => menuItem.Name == "OpenInNewTabMenuItem");
        if (item == null) return;
        var isFolder = menu.PlacementTarget is FrameworkElement
        {
            DataContext: DesktopIconViewModel { IsDirectory: true } or SearchResultViewModel { IsDirectory: true }
        } || (menu.PlacementTarget as System.Windows.Controls.ListViewItem)?.Content is SearchResultViewModel { IsDirectory: true };
        item.Visibility = isFolder ? Visibility.Visible : Visibility.Collapsed;
    }
}
