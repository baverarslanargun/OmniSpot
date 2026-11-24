using System.ComponentModel;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Threading;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.UI.Services;
using ContextMenu = System.Windows.Controls.ContextMenu;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using ListBoxItem = System.Windows.Controls.ListBoxItem;
using MenuItem = System.Windows.Controls.MenuItem;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private const int ShellActionCancelled = 1223;
    private IReadOnlySet<string> _pinnedApplications = new HashSet<string>(StringComparer.Ordinal);
    private StartMenuApplication? _menuApplication;
    private IStartMenuShell? _startMenuShell;
    private StartMenuCatalog? _startMenuCatalog;
    private IDisposable? _startMenuWatch;
    private StartMenuResults? _applicationResults;
    private DispatcherOperation? _pendingBrowserOpen;
    private ApplicationReturnState? _applicationReturn;
    private StartMenuApplications? _presentedCatalog;
    private StartMenuApplications.SearchSession? _applicationSearch;
    private string? _applicationQuery;
    private string? _presentedFileQuery;
    private bool _catalogReadyAtPresentation;
    private bool _catalogReadFailed;
    private bool _watchAttempted;

    private sealed record ApplicationReturnState(string Query, SearchSnapshot? Files, string? SelectedId, string Hint);

    private bool BrowsingApplications => _applicationReturn != null;
    private bool FileSearchReady => _isIndexed && _indexHealth is not { SearchAvailable: false };
    private bool StaleFileResults => _startMenuCatalog != null && SearchSurfaceVisible &&
        (_tabSwitchInProgress || _presentedFileQuery != SearchBox.Text);
    private bool ApplicationOwnsCommands => BrowsingApplications ||
        (StartMenuHost.Visibility == Visibility.Visible &&
         (_applicationResults?.Applications.SelectedItem != null || (_applicationResults?.IsKeyboardFocusWithin == true)));

    private void InitializeStartMenu(IStartMenuShell? shell)
    {
        AllApplicationsMenuItem.Visibility = shell == null ? Visibility.Collapsed : Visibility.Visible;
        if (shell == null) return;
        _startMenuShell = shell;
        _startMenuCatalog = shell is IStartMenuItemNotifications items
            ? new StartMenuCatalog(shell.ReadApplications, items.ReadItem, reportError: OnCatalogReadFailed)
            : new StartMenuCatalog(shell.ReadApplications, reportError: OnCatalogReadFailed);
        _startMenuCatalog.SnapshotChanged += OnApplicationCatalogChanged;
        ApplyPinnedApplications();
        SearchBox.IsEnabled = true;
        Loaded += OnApplicationWindowActivated;
        Activated += OnApplicationWindowActivated;
        ResultsList.SelectionChanged += (_, e) =>
        {
            if (e.AddedItems.Count > 0 && _applicationResults != null)
                _applicationResults.Applications.SelectedIndex = -1;
        };
        ResultsList.PreviewKeyDown += FileResultsApplicationKey;
        ResultsContainer.ContextMenuOpening += (_, e) => { if (StaleFileResults) e.Handled = true; };
    }

    private void OnApplicationWindowActivated(object? sender, EventArgs e)
    {
        if (!IsLoaded || _isPreparedForShutdown) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_isPreparedForShutdown || _startMenuCatalog == null) return;
            if (!_watchAttempted)
            {
                _watchAttempted = true;
                try
                {
                    var handle = new WindowInteropHelper(this).EnsureHandle();
                    _startMenuWatch = _startMenuShell is IStartMenuItemNotifications items
                        ? items.WatchItems(handle, _startMenuCatalog.RequestChange)
                        : _startMenuShell!.Watch(handle, _startMenuCatalog.RequestRefresh);
                }
                catch (Exception error) { Log($"Başlat uygulamaları sonraki pencere açılışlarında denetlenecek. {error.Message}"); }
            }
            _startMenuCatalog.OnActivated(hasCompleteWatch: _startMenuWatch is IStartMenuChangeWatch { IsComplete: true });
        }, DispatcherPriority.Background);
    }

    private void OnCatalogReadFailed(Exception error)
    {
        if (Dispatcher.HasShutdownStarted) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_isPreparedForShutdown) return;
            _catalogReadFailed = true;
            Log($"Başlat uygulamaları okunamadı. {error.Message}");
            ShowApplicationMatches(SearchBox.Text, refresh: true);
        });
    }

    private void OnApplicationCatalogChanged()
    {
        if (Dispatcher.HasShutdownStarted) return;
        var changedIds = _startMenuCatalog?.Current.ChangedIds;
        _ = Dispatcher.InvokeAsync(() =>
        {
            if (_isPreparedForShutdown) return;
            _catalogReadFailed = false;
            _applicationResults?.ResetIcons(changedIds);
            ShowApplicationMatches(SearchBox.Text, refresh: true);
        }, DispatcherPriority.Background);
    }

    private void StopStartMenu()
    {
        _pendingBrowserOpen?.Abort();
        Loaded -= OnApplicationWindowActivated;
        Activated -= OnApplicationWindowActivated;
        _startMenuWatch?.Dispose();
        if (_startMenuCatalog != null)
        {
            _startMenuCatalog.SnapshotChanged -= OnApplicationCatalogChanged;
            _startMenuCatalog.Dispose();
        }
        _applicationResults?.Stop();
    }

    private StartMenuResults GetApplicationResults()
    {
        if (_applicationResults != null) return _applicationResults;
        var view = new StartMenuResults(_startMenuShell!) { PinnedIds = _pinnedApplications };
        _applicationResults = view;
        StartMenuHost.Content = view;
        var menu = ApplicationMenu;
        view.Applications.ContextMenu = menu;
        view.BestMatch.ContextMenu = menu;
        view.Applications.ContextMenuOpening += (_, e) =>
        {
            if (InsideScrollBar(e.OriginalSource as DependencyObject, view.Applications)) return;
            PrepareApplicationMenu(view, e,
                (ItemsControl.ContainerFromElement(view.Applications, e.OriginalSource as DependencyObject) as ListBoxItem)?.Content
                    as StartMenuApplication);
        };
        view.BestMatch.ContextMenuOpening += (_, e) =>
            PrepareApplicationMenu(view, e, view.LeadsWithBestMatch ? view.BestMatch.DataContext as StartMenuApplication : null);
        view.ShowAll.Click += (_, _) => OpenApplicationBrowser(SearchBox.Text);
        view.BrowseBack.Click += (_, _) => CloseApplicationBrowser();
        view.Applications.SelectionChanged += (_, _) =>
        {
            if (view.Applications.SelectedItem == null) return;
            ResultsList.UnselectAll();
            _selectedItemPath = null;
            _hoveredItemPath = null;
            UpdateSelectionBar();
        };
        view.Applications.PreviewKeyDown += (_, e) => HandleApplicationKey(e);
        view.BestMatchActivated += (_, _) => LaunchSelectedApplication();
        view.Applications.MouseDoubleClick += (_, e) =>
        {
            if (ItemsControl.ContainerFromElement(view.Applications, e.OriginalSource as DependencyObject)
                is ListBoxItem { Content: StartMenuApplication app } && Equals(view.Applications.SelectedItem, app))
            {
                LaunchSelectedApplication();
                e.Handled = true;
            }
        };
        return view;
    }

    private void ShowApplicationMatches(string query, bool refresh = false)
    {
        if (_startMenuCatalog == null || _isPreparedForShutdown || _pendingBrowserOpen != null) return;
        var all = BrowsingApplications;
        if (!all && (string.IsNullOrWhiteSpace(query) || ActiveSearchScope != null ||
            _isNaturalLanguageMode || _searchView.Filter.IsActive || !_searchView.Sort.IsRelevance))
        {
            HideApplicationMatches();
            return;
        }

        var catalog = _startMenuCatalog.Current;
        var ready = !ReferenceEquals(catalog, StartMenuApplications.Empty);
        if (all && !ready && !_catalogReadFailed) return;
        var sameQuery = query == _applicationQuery;
        if (!refresh && sameQuery && ReferenceEquals(catalog, _presentedCatalog))
        {
            UpdateFileAreaForApplications();
            return;
        }
        var keepId = sameQuery && StartMenuHost.Visibility == Visibility.Visible
            ? _applicationResults?.Applications.SelectedValue as string : null;
        var offerFirst = !sameQuery || !_catalogReadyAtPresentation;
        var fileSelected = ResultsList.SelectedItem != null && _presentedFileQuery == query;
        if (_applicationSearch == null || !ReferenceEquals(catalog, _presentedCatalog))
            _applicationSearch = new StartMenuApplications.SearchSession(catalog, _pinnedApplications);
        _applicationQuery = query;
        _presentedCatalog = catalog;
        _catalogReadyAtPresentation = ready;

        var best = _applicationSearch.MatchGeneral(query);
        var items = all ? catalog.MatchAll(query, _pinnedApplications) : best.Items;
        if (!all && items.Count == 0 && FileSearchReady)
        {
            ClearApplicationRows(waitForFileResults: !refresh);
            UpdateFileAreaForApplications();
            return;
        }

        var view = GetApplicationResults();
        StartMenuHost.Visibility = Visibility.Visible;
        var lead = !all && best.UniqueFirst is { } unique && items.Count > 0 && Equals(items[0], unique) ? unique : null;
        view.UseLayout(all, lead, grid: all && _isGridViewMode);
        view.PinnedIds = _pinnedApplications;
        view.ShowAll.Content = $"Tümü ({best.TotalCount})";
        view.ShowAll.Visibility = !all && best.TotalCount > items.Count ? Visibility.Visible : Visibility.Collapsed;
        view.Applications.MaxHeight = all ? 460 : 190;
        if (all || view.Applications.Items.Count != items.Count ||
            !view.Applications.Items.Cast<StartMenuApplication>().SequenceEqual(items))
        {
            view.FinishGlide();
            view.Applications.ItemsSource = items;
        }
        view.EmptyMessage.Text = !ready
            ? (_catalogReadFailed ? "Uygulamalar okunamadı." : "Uygulamalar yükleniyor…")
            : "Eşleşen uygulama bulunamadı.";
        view.EmptyMessage.Visibility = items.Count == 0 ? Visibility.Visible : Visibility.Collapsed;
        if (keepId != null)
            view.Applications.SelectedValue = keepId;
        else if (offerFirst && !fileSelected)
            view.Applications.SelectedItem = best.UniqueFirst ?? (all && items.Count == 1 ? items[0] : null);
        else
            view.Applications.SelectedIndex = -1;
        view.UpdateStrip();
        UpdateFileAreaForApplications();
    }

    private void ClearApplicationRows(bool waitForFileResults = false)
    {
        if (StartMenuHost.Visibility == Visibility.Collapsed) return;
        StartMenuHost.Visibility = waitForFileResults && SearchSurfaceVisible ? Visibility.Hidden : Visibility.Collapsed;
        if (_applicationResults?.Applications.Items.Count > 3) _applicationResults.Applications.ItemsSource = null;
    }

    private void FinishApplicationLayout()
    {
        if (StartMenuHost.Visibility == Visibility.Hidden) StartMenuHost.Visibility = Visibility.Collapsed;
    }

    private void HideApplicationMatches()
    {
        if (_startMenuCatalog == null) return;
        _applicationQuery = null;
        _presentedCatalog = null;
        _applicationSearch = null;
        ClearApplicationRows();
        UpdateFileAreaForApplications();
    }

    private bool FileResultsPending => !string.IsNullOrWhiteSpace(SearchBox.Text) &&
        _completedSearchRequest?.Query != SearchBox.Text;

    private void UpdateFileAreaForApplications()
    {
        if (_startMenuCatalog == null) return;
        var appsVisible = StartMenuHost.Visibility == Visibility.Visible;
        if (!BrowsingApplications && FileSearchReady && FileResultsPending) return;
        var onlyApps = appsVisible && (!FileSearchReady || _completedSearchOutcome?.Results.Count == 0);
        FileContentArea.Visibility = BrowsingApplications || onlyApps ? Visibility.Collapsed : Visibility.Visible;
    }

    private void AllApplications_Click(object sender, RoutedEventArgs e) => OpenApplicationBrowser(string.Empty);

    private void OpenApplicationBrowser(string query)
    {
        if (_startMenuCatalog == null || BrowsingApplications) return;
        var selectedId = StartMenuHost.Visibility == Visibility.Visible
            ? _applicationResults?.Applications.SelectedValue as string : null;
        _applicationReturn = new(SearchBox.Text, CaptureSearchSnapshot(), selectedId, SearchWatermark.Text);
        SearchWatermark.Text = "Uygulamalarda ara...";
        CancelCurrentSearch();
        SearchLatencyText.Visibility = Visibility.Collapsed;
        _applicationQuery = null;
        SearchBox.Focus();
        GetApplicationResults().ShowBrowserLoading(_isGridViewMode);
        StartMenuHost.Visibility = Visibility.Visible;
        UpdateFileAreaForApplications();
        _pendingBrowserOpen = Dispatcher.InvokeAsync(() =>
        {
            _pendingBrowserOpen = null;
            ShowApplicationMatches(SearchBox.Text);
        }, DispatcherPriority.Background);
        SearchBox.Text = query;
        UpdateSelectionBar();
        SearchBox.CaretIndex = query.Length;
    }

    private void CloseApplicationBrowser()
    {
        if (_applicationReturn is not { } origin) return;
        _pendingBrowserOpen?.Abort();
        _pendingBrowserOpen = null;
        _applicationReturn = null;
        SearchWatermark.Text = origin.Hint;
        HideApplicationMatches();
        if (origin.Files != null) RestoreSearch(origin.Files);
        else
        {
            _restoringSearch = true;
            try { SearchBox.Text = origin.Query; }
            finally { _restoringSearch = false; }
            if (!string.IsNullOrWhiteSpace(origin.Query)) BeginSearch(origin.Query);
        }
        if (origin.SelectedId != null && StartMenuHost.Visibility == Visibility.Visible && _applicationResults != null)
            _applicationResults.Applications.SelectedValue = origin.SelectedId;
        UpdateSelectionBar();
        SearchBox.Focus();
        SearchBox.CaretIndex = SearchBox.Text.Length;
    }

    private bool HandleApplicationKey(KeyEventArgs e)
    {
        if (StartMenuHost.Visibility != Visibility.Visible || Keyboard.Modifiers != ModifierKeys.None) return false;
        if (e.Key == Key.Escape && BrowsingApplications)
            CloseApplicationBrowser();
        else if (e.Key == Key.Enter && ApplicationOwnsCommands)
            LaunchSelectedApplication();
        else if (BrowsingApplications && _applicationResults?.IsGrid == true && e.Key is Key.Up or Key.Down or Key.Left or Key.Right)
        {
            if (!MoveApplicationTile(e.Key)) return false;
            SearchBox.Focus();
        }
        else if (e.Key is Key.Up or Key.Down && MoveApplicationSelection(e.Key == Key.Down ? 1 : -1) ||
                 e.Key is Key.Left or Key.Right && MoveApplicationChip(e.Key == Key.Right ? 1 : -1))
        {
            SearchBox.Focus();
        }
        else return false;
        e.Handled = true;
        return true;
    }

    private void FileResultsApplicationKey(object sender, KeyEventArgs e)
    {
        if (NameEditing || StaleFileResults || StartMenuHost.Visibility != Visibility.Visible || e.Key != Key.Up ||
            Keyboard.Modifiers != ModifierKeys.None || ResultsList.SelectedIndex != 0) return;
        HandleApplicationKey(e);
    }

    private bool MoveApplicationSelection(int step)
    {
        var view = _applicationResults!;
        var count = view.Applications.Items.Count;
        if (count == 0) return false;
        var app = view.Applications.SelectedIndex;
        if (BrowsingApplications)
        {
            SelectApplication(app < 0 ? 0 : Math.Clamp(app + step, 0, count - 1));
            return true;
        }

        var stripStart = view.StripStart;
        var firstStripRow = count > stripStart ? stripStart : 0;
        var fileCount = HasCurrentFileSearch ? _searchResults.Count : 0;
        var file = fileCount > 0 ? ResultsList.SelectedIndex : -1;
        if (file >= 0)
        {
            if (step > 0 || file >= (_isGridViewMode ? SearchGridColumns : 1))
                SelectFileSearchResult(Math.Clamp(file + step, 0, fileCount - 1));
            else
                SelectApplication(firstStripRow);
        }
        else if (app < 0)
            SelectApplication(0);
        else if (step < 0)
            SelectApplication(stripStart > 0 && app >= stripStart ? 0 : app);
        else if (app < stripStart && count > stripStart)
            SelectApplication(stripStart);
        else if (fileCount > 0)
            SelectFileSearchResult(0);
        return true;
    }

    private bool MoveApplicationChip(int step)
    {
        var view = _applicationResults!;
        var apps = view.Applications;
        if (BrowsingApplications || apps.SelectedIndex < view.StripStart) return false;
        SelectApplication(Math.Clamp(apps.SelectedIndex + step, view.StripStart, apps.Items.Count - 1));
        return true;
    }

    private bool MoveApplicationTile(Key key)
    {
        var apps = _applicationResults!.Applications;
        var count = apps.Items.Count;
        if (count == 0) return false;
        var index = apps.SelectedIndex;
        if (index < 0)
        {
            if (key is Key.Left or Key.Right) return false;
            SelectApplication(0);
            return true;
        }
        SelectApplication(NavigationTarget(key, index, StartMenuResults.GridColumns, count, 1) ?? index);
        return true;
    }

    private void SelectApplication(int index)
    {
        var view = _applicationResults!;
        var apps = view.Applications;
        if (index < 0 || index >= apps.Items.Count) return;
        var from = apps.SelectedIndex;
        var source = Animate && from >= 0 && from != index ? view.SelectionSurface(from) : null;
        var arrive = source != null ? view.HoldSelection(index) : null;
        apps.SelectedIndex = index;
        BringApplicationIntoView(index);
        if (arrive != null && view.SelectionSurface(index) is { } target && view.Glide.Start(source!, target, arrive))
            return;
        arrive?.Invoke();
    }

    private void BringApplicationIntoView(int index)
    {
        var apps = _applicationResults!.Applications;
        var viewer = FindDescendant<ScrollViewer>(apps);
        if (Animate && viewer != null &&
            apps.ItemContainerGenerator.ContainerFromIndex(index) is FrameworkElement item &&
            SmoothScroll.BringIntoView(viewer, item, animate: true))
            return;
        if (viewer != null) SmoothScroll.To(viewer, viewer.VerticalOffset, animate: false);
        apps.ScrollIntoView(apps.SelectedItem);
    }

    private void RefreshApplicationLayout()
    {
        if (!BrowsingApplications) return;
        ShowApplicationMatches(SearchBox.Text, refresh: true);
        if (_applicationResults?.Applications is { SelectedItem: { } selected } apps) apps.ScrollIntoView(selected);
    }

    private void ApplyPinnedApplications()
    {
        var pinned = new HashSet<string>(_appSettings.PinnedApplications ?? [], StringComparer.Ordinal);
        if (pinned.SetEquals(_pinnedApplications)) return;
        _pinnedApplications = pinned;
        _applicationSearch = null;
        if (_applicationResults != null) _applicationResults.PinnedIds = pinned;
        if (StartMenuHost.Visibility == Visibility.Visible) ShowApplicationMatches(SearchBox.Text, refresh: true);
    }

    private ContextMenu ApplicationMenu => (ContextMenu)FindResource("ApplicationContextMenu");

    private static bool InsideScrollBar(DependencyObject? source, DependencyObject owner)
    {
        for (var current = source; current != null && !ReferenceEquals(current, owner);
             current = current is System.Windows.Media.Visual ? System.Windows.Media.VisualTreeHelper.GetParent(current)
                 : LogicalTreeHelper.GetParent(current))
        {
            if (current is System.Windows.Controls.Primitives.ScrollBar) return true;
        }
        return false;
    }

    private void PrepareApplicationMenu(StartMenuResults view, ContextMenuEventArgs e, StartMenuApplication? app)
    {
        if (app == null || _isPreparedForShutdown || _tabSwitchInProgress)
        {
            e.Handled = true;
            return;
        }
        if (!Equals(view.Applications.SelectedItem, app)) view.Applications.SelectedItem = app;
        _menuApplication = app;
        var items = ApplicationMenu.Items.OfType<MenuItem>().ToDictionary(item => item.Name);
        var actions = _startMenuShell is IStartMenuApplicationActions;
        items["ApplicationRunAsMenuItem"].Visibility = actions && app.CanRunAsAdministrator ? Visibility.Visible : Visibility.Collapsed;
        items["ApplicationLocationMenuItem"].Visibility = actions && app.HasFileLocation ? Visibility.Visible : Visibility.Collapsed;
        items["ApplicationUninstallMenuItem"].Visibility = actions ? Visibility.Visible : Visibility.Collapsed;
        items["ApplicationPinMenuItem"].Header = _pinnedApplications.Contains(app.Id) ? "Sabitlemeyi Kaldır" : "OmniSpot'ta Sabitle";
    }

    private void ApplicationMenu_Open(object sender, RoutedEventArgs e)
    {
        if (_menuApplication is { } app) LaunchApplication(app);
    }

    private void ApplicationMenu_RunAs(object sender, RoutedEventArgs e)
    {
        if (_menuApplication is { } app)
            RunApplicationAction(actions => actions.RunAsAdministrator(app), $"“{app.Name}” yönetici olarak açılamadı.", launches: true);
    }

    private void ApplicationMenu_OpenLocation(object sender, RoutedEventArgs e)
    {
        if (_menuApplication is { } app)
            RunApplicationAction(actions => actions.OpenFileLocation(app), $"“{app.Name}” konumu açılamadı.", launches: false);
    }

    private void ApplicationMenu_Uninstall(object sender, RoutedEventArgs e) =>
        RunApplicationAction(actions => actions.OpenInstalledApplications(), "Yüklü uygulamalar sayfası açılamadı.", launches: false);

    private void ApplicationMenu_TogglePin(object sender, RoutedEventArgs e)
    {
        if (_menuApplication is not { } app) return;
        var settings = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(_appSettings))!;
        settings.PinnedApplications ??= [];
        var unpinned = settings.PinnedApplications.RemoveAll(id => id == app.Id) > 0;
        if (!unpinned) settings.PinnedApplications.Add(app.Id);
        try
        {
            _settingsApplication.Save(settings);
        }
        catch (Exception error)
        {
            Log($"❌ Tercih kaydedilemedi: {error.Message}");
            ShowErrorToast($"Tercih kaydedilemedi: {error.Message}");
            return;
        }
        _appSettings.PinnedApplications = settings.PinnedApplications;
        ApplyPinnedApplications();
        if (_applicationResults?.Applications is { SelectedItem: { } selected } apps) apps.ScrollIntoView(selected);
        NotifyFileTransfer(unpinned ? $"“{app.Name}” sabitlemesi kaldırıldı." : $"“{app.Name}” sabitlendi.");
    }

    private async void RunApplicationAction(Action<IStartMenuApplicationActions> action, string failure, bool launches)
    {
        if (_isPreparedForShutdown || _startMenuShell is not IStartMenuApplicationActions actions) return;
        if (launches && _measurementRun != null)
        {
            NotifyFileTransfer("Ölçüm oturumunda uygulama açma kapalı.");
            return;
        }
        try
        {
            await RunOnShellThread(() => action(actions));
        }
        catch (Win32Exception error) when (error.NativeErrorCode == ShellActionCancelled)
        {
        }
        catch (Exception error)
        {
            if (_isPreparedForShutdown) return;
            Log($"Başlat uygulaması işlemi tamamlanamadı. {error.Message}");
            NotifyFileTransfer(failure);
        }
    }

    internal static Task RunOnShellThread(Action action)
    {
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var worker = new Thread(() =>
        {
            try
            {
                action();
                completion.SetResult();
            }
            catch (Exception error)
            {
                completion.SetException(error);
            }
        }) { IsBackground = true, Name = "OmniSpot application action" };
        worker.SetApartmentState(ApartmentState.STA);
        worker.Start();
        return completion.Task;
    }

    private void LaunchSelectedApplication()
    {
        if (_isPreparedForShutdown || _tabSwitchInProgress || StartMenuHost.Visibility != Visibility.Visible ||
            _applicationQuery != SearchBox.Text ||
            _applicationResults?.Applications.SelectedItem is not StartMenuApplication app) return;
        LaunchApplication(app);
    }

    private void LaunchApplication(StartMenuApplication app)
    {
        if (_isPreparedForShutdown) return;
        if (_measurementRun != null)
        {
            NotifyFileTransfer("Ölçüm oturumunda uygulama açma kapalı.");
            return;
        }
        try { _startMenuShell!.Launch(app); }
        catch (Exception error)
        {
            Log($"Başlat uygulaması açılamadı. {error.Message}");
            NotifyFileTransfer($"“{app.Name}” açılamadı. Uygulama listesi yenileniyor.");
            _startMenuCatalog!.RequestRefresh();
        }
    }
}
