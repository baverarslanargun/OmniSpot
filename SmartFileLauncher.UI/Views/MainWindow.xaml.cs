using System.Diagnostics;
using System.Windows.Threading;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Text.Json;
using System.IO;
using SmartFileLauncher.Core.Application.Connectivity;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Refresh;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow : Window {
    private readonly MainWindowViewModel _viewModel;
    private readonly ISettingsApplicationService _settingsApplication;
    private readonly IIndexMaintenanceService _indexMaintenance;
    private readonly IIndexLifecycleService _indexLifecycle;
    private readonly ISearchApplicationService _searchService;
    private readonly ISearchDiagnosticsService _searchDiagnostics;
    private readonly IThumbnailService _thumbnailService;
    private readonly IFolderNavigationService _folderNavigation;
    private readonly IConnectivityMonitor _connectivityMonitor;
    private readonly IFileOperationService _fileOperations;
    private readonly IApplicationShellService _shellService;
    private readonly ApplicationLog _applicationLog;
    private string _desktopPath {
        get => _viewModel.DesktopPath;
        set => _viewModel.DesktopPath = value;
    }
    private string? _currentFolderPath {
        get => _viewModel.CurrentFolderPath;
        set => _viewModel.CurrentFolderPath = value;
    }
    private List<string> _indexedRootPaths {
        get => _viewModel.IndexedRootPaths;
        set => _viewModel.IndexedRootPaths = value;
    }
    private ObservableCollection<DesktopIconViewModel> _desktopIcons =>
        _viewModel.DesktopIcons;
    private ObservableCollection<SearchResultViewModel> _searchResults =>
        _viewModel.SearchResults;
    private bool _isIndexed {
        get => _viewModel.IsIndexed;
        set => _viewModel.IsIndexed = value;
    }
    private bool _isNaturalLanguageMode {
        get => _viewModel.IsNaturalLanguageMode;
        set => _viewModel.IsNaturalLanguageMode = value;
    }
    private bool _isGridViewMode {
        get => _viewModel.IsGridViewMode;
        set => _viewModel.IsGridViewMode = value;
    }
    private System.Threading.Timer? _fileChangeDebounceTimer;
    private readonly object _fileChangeTimerLock = new();
    private readonly RefreshCoalescer _fileChangeRefresh = new();
    private const int FILE_CHANGE_DEBOUNCE_MS = 1000;
    private CancellationTokenSource? _currentSearchCancellation;
    private CancellationTokenSource? _folderLoadCancellation;
    private ThumbnailViewportScheduler? _thumbnailViewport;
    private readonly System.Windows.Threading.DispatcherTimer _viewportDebounce = new();
    private readonly System.Windows.Threading.DispatcherTimer _thumbnailIdleRelease = new();
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private long _searchVersion;
    private volatile bool _isPreparedForShutdown;
    private string _lastSearchQuery {
        get => _viewModel.LastSearchQuery;
        set => _viewModel.LastSearchQuery = value;
    }
    private string? _selectedItemPath {
        get => _viewModel.SelectedItemPath;
        set => _viewModel.SelectedItemPath = value;
    }
    private string? _clipboardPath {
        get => _viewModel.ClipboardPath;
        set => _viewModel.ClipboardPath = value;
    }
    private bool _isCutOperation {
        get => _viewModel.IsCutOperation;
        set => _viewModel.IsCutOperation = value;
    }
    private string? _hoveredItemPath {
        get => _viewModel.HoveredItemPath;
        set => _viewModel.HoveredItemPath = value;
    }
    private DesktopIconViewModel? _hoveredItem {
        get => _viewModel.HoveredItem;
        set => _viewModel.HoveredItem = value;
    }
    private DesktopIconViewModel? _cutItem {
        get => _viewModel.CutItem;
        set => _viewModel.CutItem = value;
    }
    private const int DEBOUNCE_DELAY_MS = 1200;
    private const int LIVE_DEBOUNCE_MS = 120;
    private const int THUMBNAIL_SIZE = 128;
    
    private AppSettings _appSettings;
    
    public MainWindow(
        MainWindowViewModel viewModel,
        AppSettings appSettings,
        ISettingsApplicationService settingsApplication,
        IIndexMaintenanceService indexMaintenance,
        IIndexLifecycleService indexLifecycle,
        ISearchApplicationService searchService,
        ISearchDiagnosticsService searchDiagnostics,
        IThumbnailService thumbnailService,
        IFolderNavigationService folderNavigation,
        IConnectivityMonitor connectivityMonitor,
        IFileOperationService fileOperations,
        IApplicationShellService shellService,
        ApplicationLog applicationLog,
        ApplicationStartupOptions startupOptions,
        MeasurementRunLayout? measurementRun) {
        _viewModel = viewModel ?? throw new ArgumentNullException(nameof(viewModel));
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _settingsApplication = settingsApplication ?? throw new ArgumentNullException(nameof(settingsApplication));
        _indexMaintenance = indexMaintenance ?? throw new ArgumentNullException(nameof(indexMaintenance));
        _indexLifecycle = indexLifecycle ?? throw new ArgumentNullException(nameof(indexLifecycle));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _searchDiagnostics = searchDiagnostics ?? throw new ArgumentNullException(nameof(searchDiagnostics));
        _thumbnailService = thumbnailService ?? throw new ArgumentNullException(nameof(thumbnailService));
        _folderNavigation = folderNavigation ?? throw new ArgumentNullException(nameof(folderNavigation));
        _connectivityMonitor = connectivityMonitor ?? throw new ArgumentNullException(nameof(connectivityMonitor));
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _shellService = shellService ?? throw new ArgumentNullException(nameof(shellService));
        _applicationLog = applicationLog ?? throw new ArgumentNullException(nameof(applicationLog));
        _startupOptions = startupOptions ?? throw new ArgumentNullException(nameof(startupOptions));
        _measurementRun = measurementRun;

        DataContext = _viewModel;
        InitializeComponent();
        TrackMoreOptionsMenuState();
        SetLoadingIndeterminate(true);
        DesktopIcons.ItemsSource = _desktopRows;
        _desktopIcons.CollectionChanged += (_, _) => QueueDesktopRowsRebuild();
        _folderLoadingDelay.Tick += (_, _) => ShowFolderLoadingBarNow();

        _indexLifecycle.ProgressChanged += HandleIndexProgress;
        _indexLifecycle.Error += HandleIndexError;
        _indexLifecycle.Notice += HandleIndexNotice;
        _indexLifecycle.FileChanged += HandleFileSystemChange;
        _indexLifecycle.ReconciliationProgressChanged += HandleReconciliationProgress;
        _indexLifecycle.ReconciliationStateChanged += HandleReconciliationStateChanged;
        _shellService.ToggleRequested += HandleShellToggleRequested;
        _shellService.ShowRequested += HandleShellShowRequested;
        _shellService.SettingsRequested += HandleShellSettingsRequested;
        _shellService.ExitRequested += HandleShellExitRequested;
        SourceInitialized += HandleSourceInitialized;
        
        SearchBox.TextChanged += SearchBox_TextChanged;
        SearchBox.TextChanged += (_, __) => SearchWatermark.Visibility =
            string.IsNullOrEmpty(SearchBox.Text) ? Visibility.Visible : Visibility.Collapsed;
        SearchBox.KeyDown += SearchBox_KeyDown;
        ResultsList.MouseDoubleClick += (_, __) => OpenSelected();
        ConsoleToggleButton.Click += (_, __) => ToggleDiagnosticsWindow();
        InitializeDiagnostics();
        NaturalLanguageToggle.Checked += (_, __) => EnableNaturalLanguageMode();
        NaturalLanguageToggle.Unchecked += (_, __) => DisableNaturalLanguageMode();
        
        Log("=== OmniSpot Başlatıldı ===");
        Log("OmniSpot: Hafif Basit Masaüstü ve Tarayıcı");
        
        Closing += MainWindow_Closing;
        
        ApplyDefaultSettings();
        LoadAiEffortSelection();
        
        InitializeThumbnailViewport();

        Loaded += MainWindow_Loaded;
    }

    private async void MainWindow_Loaded(object sender, RoutedEventArgs e) {
        Loaded -= MainWindow_Loaded;

        try {
            await InitializeConnectivityAsync();
            if (!_isPreparedForShutdown) {
                await InitializeAsync();
            }
        } catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested) {
        }
    }
    
    private void ApplyDefaultSettings() {
        if (_appSettings.NaturalLanguageModeEnabled) {
            NaturalLanguageToggle.IsChecked = true;
        }
        if (_appSettings.GridViewEnabled) {
            EnableGridView();
        } else {
            ApplyDesktopLayout();
        }
        if (_appSettings.StartMinimized) {
            WindowState = WindowState.Minimized;
            if (_appSettings.MinimizeToTrayOnClose) {
                Hide();
            }
        }
    }
    
    private void HandleSourceInitialized(object? sender, EventArgs e) {
        _shellService.Initialize(this, _appSettings);
        ApplyWindowCorners();
    }

    [System.Runtime.InteropServices.DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    private void ApplyWindowCorners() {
        if (Environment.OSVersion.Version.Build < 22000) return;
        var hwnd = new System.Windows.Interop.WindowInteropHelper(this).Handle;
        if (hwnd == IntPtr.Zero) return;
        var preference = 2;
        DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
    }

    private void ShowPanel(FrameworkElement panel) {
        if (panel.Visibility == Visibility.Visible) return;
        panel.Visibility = Visibility.Visible;
        AnimatePanel(panel, 0, 6, 0);
    }

    private void HandleShellToggleRequested() {
        Dispatcher.Invoke(() => {
            if (WindowState == WindowState.Minimized || !IsVisible) {
                ShowAndActivate();
            } else {
                MinimizeToTray();
            }
        });
    }

    private void HandleShellShowRequested() {
        Dispatcher.Invoke(ShowAndActivate);
    }

    private void HandleShellSettingsRequested() {
        Dispatcher.Invoke(OpenSettings);
    }

    private void HandleShellExitRequested() {
        Dispatcher.Invoke(ForceExit);
    }
    private void ShowAndActivate() {
        var wasHidden = !IsVisible || WindowState == WindowState.Minimized;
        Show();
        WindowState = WindowState.Normal;
        if (wasHidden) {
            Shell.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1,
                TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 120 : 0)));
        }
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        Log("🔔 OmniSpot uyandırıldı");
    }
    
    private void MinimizeToTray() {
        if (_appSettings.MinimizeToTrayOnClose) {
            Hide();
        } else {
            WindowState = WindowState.Minimized;
        }
    }
    
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
        if (_appSettings.MinimizeToTrayOnClose) {
            e.Cancel = true;
            MinimizeToTray();
            Log("📌 OmniSpot system tray'e küçültüldü");
        }
    }
    
    private void OpenSettings() {
        _shellService.SuspendHotkey();
        
        var settingsWindow = new SettingsWindow(
            _appSettings,
            _settingsApplication,
            _indexMaintenance,
            Log,
            () => _thumbnailService.GetDiagnostics().DecodedBytes);
        settingsWindow.Owner = this;
        settingsWindow.SettingsChanged += OnSettingsChanged;
        settingsWindow.IndexRebuildRequested += OnIndexRebuildRequested;
        settingsWindow.RestartRequested += OnRestartRequested;
        settingsWindow.ShowDialog();
        
        if (!_isPreparedForShutdown) {
            _shellService.ApplyHotkey(_appSettings);
        }
    }
    
    private void OnSettingsChanged(object? sender, AppSettings newSettings) {
        _appSettings = newSettings;
        LoadAiEffortSelection();
        ApplyThumbnailSettings();
        Log("⚙️ Ayarlar güncellendi");
    }

    private void OnIndexRebuildRequested(object? sender, EventArgs e) {
        ForceExit();
    }

    private void OnRestartRequested(object? sender, EventArgs e) {
        ForceExit();
    }
    
    private void ForceExit() {
        PrepareForShutdown();
        System.Windows.Application.Current.Shutdown();
    }

    internal void PrepareForShutdown() {
        if (_isPreparedForShutdown) return;
        _isPreparedForShutdown = true;

        RecordMeasurementEvent("kapanış başladı");

        _lifetimeCancellation.Cancel();
        ShutdownThumbnailPolicy();
        _viewportDebounce.Stop();
        _thumbnailIdleRelease.Stop();
        _thumbnailViewport?.Cancel();
        CancelCurrentSearch();
        var folderCancellation = Interlocked.Exchange(
            ref _folderLoadCancellation,
            null);
        try {
            folderCancellation?.Cancel();
        } finally {
            folderCancellation?.Dispose();
        }

        ShutdownDiagnostics();
        _indexLifecycle.ProgressChanged -= HandleIndexProgress;
        _indexLifecycle.Error -= HandleIndexError;
        _indexLifecycle.Notice -= HandleIndexNotice;
        _indexLifecycle.FileChanged -= HandleFileSystemChange;
        _indexLifecycle.ReconciliationProgressChanged -= HandleReconciliationProgress;
        _indexLifecycle.ReconciliationStateChanged -= HandleReconciliationStateChanged;
        _connectivityMonitor.ConnectivityChanged -= HandleConnectivityChanged;
        _shellService.ToggleRequested -= HandleShellToggleRequested;
        _shellService.ShowRequested -= HandleShellShowRequested;
        _shellService.SettingsRequested -= HandleShellSettingsRequested;
        _shellService.ExitRequested -= HandleShellExitRequested;
        SourceInitialized -= HandleSourceInitialized;
        
        lock (_fileChangeTimerLock) {
            _fileChangeDebounceTimer?.Dispose();
            _fileChangeDebounceTimer = null;
        }
        Closing -= MainWindow_Closing;
    }
    
    private async Task InitializeConnectivityAsync() {
        var isConnected = await _connectivityMonitor.CheckNowAsync(
            _lifetimeCancellation.Token);
        UpdateAIButtonState(isConnected);
        _connectivityMonitor.ConnectivityChanged += HandleConnectivityChanged;
        _connectivityMonitor.Start();
    }

    private void HandleConnectivityChanged(bool isConnected) {
        if (_isPreparedForShutdown ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished) return;

        Dispatcher.BeginInvoke(new Action(() => {
            if (_isPreparedForShutdown) return;
            UpdateAIButtonState(isConnected);
            Log(isConnected
                ? "🌐 İnternet bağlantısı sağlandı"
                : "⚠️ İnternet bağlantısı kesildi");
        }));
    }
    
    private void UpdateAIButtonState(bool isConnected) {
        NaturalLanguageToggle.IsEnabled = isConnected;
        
        if (!isConnected && _isNaturalLanguageMode) {
            NaturalLanguageToggle.IsChecked = false;
            _isNaturalLanguageMode = false;
            SearchWatermark.Text = "OmniSpot: Hafif Basit Masaüstü ve Tarayıcı";
            Log("⚠️ İnternet yok, AI modu kapatıldı");
        }
    }
    
    private void Border_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (e.ChangedButton == MouseButton.Left) {
            this.DragMove();
        }
    }
    
    private void Log(string message) {
        _applicationLog.Write(message);
    }

    private void HandleIndexProgress(IndexProgress progress) {
        if (_isPreparedForShutdown) return;

        Dispatcher.BeginInvoke(new Action(() => {
            if (progress.IsCatalogBuild) {
                EnterCatalogBuildStage();
                return;
            }

            SetLoadingStatus(progress.Status);
            if (progress.IsIndeterminate) {
                SetLoadingIndeterminate(true);
                return;
            }

            SetLoadingIndeterminate(false);
            if (progress.Percentage >= 0 && progress.Percentage <= 100) {
                SetLoadingPercentage(progress.Percentage);
            }
        }));
    }

    private const double LoadingTrackWidth = 240;
    private const int SkeletonCardCount = 8;
    private bool _loadingIndeterminate;
    private bool _catalogBuildStageShown;
    private string? _loadingStatusText;

    private static bool Animate => SystemParameters.ClientAreaAnimation;

    private static System.Windows.Media.Animation.DoubleAnimation Loop(double from, double to, TimeSpan duration, System.Windows.Media.Animation.IEasingFunction? ease = null) =>
        new(from, to, duration) { RepeatBehavior = System.Windows.Media.Animation.RepeatBehavior.Forever, EasingFunction = ease };

    private static void StartPulse(System.Windows.Media.TranslateTransform shift, double pulseWidth, double trackWidth) {
        if (!Animate) {
            shift.X = trackWidth / 2 - pulseWidth / 2;
            return;
        }
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, Loop(-pulseWidth, trackWidth, TimeSpan.FromSeconds(1.2),
            new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }));
    }

    private static void StopPulse(System.Windows.Media.TranslateTransform shift) {
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
    }

    private void SetLoadingStatus(string status) {
        if (string.Equals(_loadingStatusText, status, StringComparison.Ordinal)) return;
        _loadingStatusText = status;
        LoadingStatus.Text = status;
    }

    private void SetLoadingPercentage(int percentage) {
        var width = LoadingTrackWidth * percentage / 100.0;
        if (!Animate) {
            LoadingProgressFill.Width = width;
            return;
        }
        LoadingProgressFill.BeginAnimation(WidthProperty, new System.Windows.Media.Animation.DoubleAnimation(width, TimeSpan.FromMilliseconds(220)) {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        });
    }

    private void SetLoadingIndeterminate(bool indeterminate) {
        if (_loadingIndeterminate == indeterminate) return;
        _loadingIndeterminate = indeterminate;
        if (indeterminate) {
            LoadingProgressFill.BeginAnimation(WidthProperty, null);
            LoadingProgressFill.Width = 0;
            LoadingProgressPulse.Visibility = Visibility.Visible;
            StartPulse(LoadingProgressPulseShift, 96, LoadingTrackWidth);
            return;
        }
        StopPulse(LoadingProgressPulseShift);
        LoadingProgressPulse.Visibility = Visibility.Collapsed;
    }

    private void EnterCatalogBuildStage() {
        if (_catalogBuildStageShown || _isPreparedForShutdown) return;
        _catalogBuildStageShown = true;
        SkeletonCards.ItemsSource ??= Enumerable.Range(0, SkeletonCardCount).ToList();
        StartShimmer();
        SkeletonPanel.Opacity = 1;
        SkeletonPanel.Visibility = Visibility.Visible;
        HideLoadingOverlay();
        CatalogBuildPanel.Visibility = Visibility.Visible;
        StartPulse(CatalogBuildPulseShift, 140, CatalogBuildPanel.ActualWidth > 0 ? CatalogBuildPanel.ActualWidth : 600);
    }

    private System.Windows.Media.TranslateTransform? _shimmerShift;

    private void StartShimmer() {
        if (!Animate || _shimmerShift != null) return;
        var shift = new System.Windows.Media.TranslateTransform(-1, 0);
        var brush = new System.Windows.Media.LinearGradientBrush {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 0),
            MappingMode = System.Windows.Media.BrushMappingMode.RelativeToBoundingBox,
            RelativeTransform = shift
        };
        brush.GradientStops.Add(new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6EBF3"), 0));
        brush.GradientStops.Add(new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#F9FBFE"), 0.5));
        brush.GradientStops.Add(new System.Windows.Media.GradientStop((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString("#E6EBF3"), 1));
        Resources["ShimmerBrush"] = brush;
        _shimmerShift = shift;
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, Loop(-1, 1, TimeSpan.FromSeconds(1.4),
            new System.Windows.Media.Animation.SineEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseInOut }));
    }

    private void StopShimmer() {
        _shimmerShift?.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, null);
        _shimmerShift = null;
    }

    private void LeaveCatalogBuildStage() {
        StopPulse(CatalogBuildPulseShift);
        CatalogBuildPanel.Visibility = Visibility.Collapsed;
        if (SkeletonPanel.Visibility != Visibility.Visible) return;
        if (!Animate) {
            StopShimmer();
            SkeletonPanel.Visibility = Visibility.Collapsed;
            return;
        }
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(320)) {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
        };
        fade.Completed += (_, _) => {
            StopShimmer();
            SkeletonPanel.Visibility = Visibility.Collapsed;
            SkeletonPanel.BeginAnimation(OpacityProperty, null);
            SkeletonPanel.Opacity = 1;
        };
        SkeletonPanel.BeginAnimation(OpacityProperty, fade);
    }

    private void HideLoadingOverlay() {
        if (LoadingOverlay.Visibility != Visibility.Visible) return;
        StopPulse(LoadingProgressPulseShift);
        if (!Animate) {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            return;
        }
        LoadingOverlay.IsHitTestVisible = false;
        var fade = new System.Windows.Media.Animation.DoubleAnimation(1, 0, TimeSpan.FromMilliseconds(220)) {
            EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseIn }
        };
        fade.Completed += (_, _) => {
            LoadingOverlay.Visibility = Visibility.Collapsed;
            LoadingOverlay.BeginAnimation(OpacityProperty, null);
            LoadingOverlay.Opacity = 1;
        };
        LoadingOverlay.BeginAnimation(OpacityProperty, fade);
    }

    private void HandleIndexError(string error) {
        Log($"⚠️ {error}");
    }

    private void HandleIndexNotice(string notice) {
        Log($"🔗 {notice}");
    }

    private void HandleReconciliationProgress(
        int processed,
        int total,
        int percentage) {
        if (_isPreparedForShutdown) return;

        Dispatcher.BeginInvoke(new Action(() =>
            UpdateDeltaSyncProgress(processed, total, percentage)));
    }

    private void HandleReconciliationStateChanged(bool isRunning) {
        if (_isPreparedForShutdown) return;

        Dispatcher.BeginInvoke(new Action(() => UpdateDeltaSyncState(isRunning)));
    }
    
    private void EnableNaturalLanguageMode() {
        _isNaturalLanguageMode = true;
        SearchWatermark.Text = "🤖 OmniSpot AI - Doğal dil ile ara";
        Log("🤖 Doğal dil modu aktif");
    }

    private void LoadAiEffortSelection() {
        _appSettings.AiReasoningEffort = _appSettings.AiReasoningEffort is "low" or "medium" or "high"
            ? _appSettings.AiReasoningEffort : "medium";
        var effort = _appSettings.AiReasoningEffort;
        var label = effort switch { "low" => "Low", "high" => "High", _ => "Medium" };
        AiEffortLabel.Text = label;
        AiEffortButton.ToolTip = $"AI eforu: {label}\nTıkla: Low → Medium → High\nYüksek efor daha uzun sürebilir.";
        System.Windows.Automation.AutomationProperties.SetName(AiEffortButton, $"AI eforu: {label}. Sonraki seviyeye geç");
        var angle = effort switch { "low" => -55d, "high" => 55d, _ => 0d };
        AiEffortNeedle.BeginAnimation(System.Windows.Media.RotateTransform.AngleProperty,
            new System.Windows.Media.Animation.DoubleAnimation(angle, TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 120 : 0)));
    }

    private void AiEffortButton_Click(object sender, RoutedEventArgs e) {
        var effort = _appSettings.AiReasoningEffort switch { "low" => "medium", "medium" => "high", _ => "low" };
        _appSettings.AiReasoningEffort = effort;
        LoadAiEffortSelection();
        try {
            _settingsApplication.Save(_appSettings);
        } catch (Exception ex) {
            Log($"⚠️ AI efor tercihi kaydedilemedi: {ex.Message}");
        }
        Log($"🤖 AI eforu: {effort}");
        SearchBox.Focus();
        if (_isNaturalLanguageMode && _isIndexed && !string.IsNullOrWhiteSpace(SearchBox.Text)) {
            BeginSearch(SearchBox.Text.Trim(), debounce: true);
        }
    }
    
    private void DisableNaturalLanguageMode() {
        _isNaturalLanguageMode = false;
        SearchWatermark.Text = "OmniSpot: Hafif Basit Masaüstü ve Tarayıcı";
        Log("📝 Standart arama modu aktif");
    }
    
    private void EnableGridView() {
        _isGridViewMode = true;
        ApplyViewMode(ResultsList, ResultsGridScroll);
        Log("⊞ Grid görünümü aktif");
    }

    private void DisableGridView() {
        _isGridViewMode = false;
        ApplyViewMode(ResultsGridScroll, ResultsList);
        Log("☰ Liste görünümü aktif");
    }

    private void ApplyViewMode(UIElement hide, UIElement show) {
        var duration = TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 160 : 0);
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

        hide.Visibility = Visibility.Collapsed;
        show.Visibility = Visibility.Visible;
        if (show is FrameworkElement shown) AnimatePanel(shown, 0, 0, 0.35);

        var thumbShift = new System.Windows.Media.Animation.DoubleAnimation(_isGridViewMode ? 28 : 0, duration) { EasingFunction = ease };
        ViewModeThumbShift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, thumbShift);
        var active = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x00, 0x7A, 0xFF));
        var idle = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x94, 0xA3, 0xB8));
        ViewModeListIcon.Stroke = _isGridViewMode ? idle : active;
        ViewModeGridIcon.Stroke = _isGridViewMode ? active : idle;
        ApplyDesktopLayout();
        if (DesktopIconsScroll.Visibility == Visibility.Visible) AnimatePanel(DesktopIconsScroll, 0, 0, 0.35);
        var current = _isGridViewMode ? "Grid" : "Liste";
        var next = _isGridViewMode ? "Liste" : "Grid";
        ViewModeMenuItem.ToolTip = $"Görünüm: {current}. Tıkla: {next} görünümüne geç";
        System.Windows.Automation.AutomationProperties.SetName(ViewModeMenuItem, $"Görünüm: {current}. {next} görünümüne geç");
    }

    private DateTime _moreOptionsMenuClosedAt = DateTime.MinValue;

    private void TrackMoreOptionsMenuState() {
        var menu = MoreOptionsButton.ContextMenu;
        System.ComponentModel.DependencyPropertyDescriptor
            .FromProperty(ContextMenu.IsOpenProperty, typeof(ContextMenu))
            .AddValueChanged(menu, (_, _) => {
                if (menu.IsOpen) return;
                _moreOptionsMenuClosedAt = DateTime.UtcNow;
                if (!_isPreparedForShutdown) SearchBox.Focus();
            });
    }

    private const int DesktopGridColumns = 4;
    private readonly ObservableCollection<DesktopRowViewModel> _desktopRows = new();
    private bool _desktopRowsRebuildQueued;
    private ScrollViewer? _desktopScroll;

    private int DesktopColumns => _isGridViewMode ? DesktopGridColumns : 1;

    private ScrollViewer? DesktopScroll => _desktopScroll ??= FindDescendant<ScrollViewer>(DesktopIcons);

    private static T? FindDescendant<T>(DependencyObject root) where T : DependencyObject {
        var count = System.Windows.Media.VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++) {
            var child = System.Windows.Media.VisualTreeHelper.GetChild(root, i);
            if (child is T match) return match;
            var nested = FindDescendant<T>(child);
            if (nested != null) return nested;
        }
        return null;
    }

    private void QueueDesktopRowsRebuild() {
        if (_desktopRowsRebuildQueued || _isPreparedForShutdown) return;
        _desktopRowsRebuildQueued = true;
        Dispatcher.BeginInvoke(new Action(RebuildDesktopRows), System.Windows.Threading.DispatcherPriority.Loaded);
    }

    private void RebuildDesktopRows() {
        _desktopRowsRebuildQueued = false;
        if (_isPreparedForShutdown) return;
        var columns = DesktopColumns;
        var source = _desktopIcons;
        var rows = new List<DesktopRowViewModel>((source.Count + columns - 1) / columns);
        for (var i = 0; i < source.Count; i += columns) {
            var take = Math.Min(columns, source.Count - i);
            var slice = new DesktopIconViewModel[take];
            for (var j = 0; j < take; j++) slice[j] = source[i + j];
            rows.Add(new DesktopRowViewModel(slice));
        }
        _desktopRows.Clear();
        foreach (var row in rows) _desktopRows.Add(row);
        ScheduleViewportUpdate();
    }

    private void ApplyDesktopLayout() {
        DesktopIcons.ItemTemplate = (DataTemplate)FindResource(_isGridViewMode ? "DesktopGridRowTemplate" : "DesktopListRowTemplate");
        RebuildDesktopRows();
    }

    private void MoreOptionsButton_Click(object sender, RoutedEventArgs e) {
        var menu = MoreOptionsButton.ContextMenu;
        if (menu.IsOpen || (DateTime.UtcNow - _moreOptionsMenuClosedAt) < TimeSpan.FromMilliseconds(250)) {
            menu.IsOpen = false;
            SearchBox.Focus();
            return;
        }
        menu.PlacementTarget = MoreOptionsButton;
        menu.IsOpen = true;
    }

    private void ViewModeMenuItem_Click(object sender, RoutedEventArgs e) {
        if (_isGridViewMode) DisableGridView(); else EnableGridView();
    }
    
    private async Task InitializeAsync() {
        try {
            RecordMeasurementEvent(
                "indeks başlatma başladı",
                _startupOptions.ProfileName);
            Log("=== İndeksleme Başlıyor ===");
            Log($"📦 Database: {_indexLifecycle.DatabasePath}");

            var stopwatch = Stopwatch.StartNew();
            var startup = await _indexLifecycle.InitializeAsync(
                _lifetimeCancellation.Token);
            stopwatch.Stop();

            _desktopPath = startup.DesktopPath;
            _indexedRootPaths = startup.RootPaths.ToList();

            foreach (var rootPath in startup.RootPaths) {
                Log($"📂 İndeks kökü: {rootPath}");
            }
            Log($"📊 Toplam {startup.RootPaths.Count} dizin tarandı");

            await Dispatcher.InvokeAsync(() =>
                CompleteIndexInitialization(startup, stopwatch.ElapsedMilliseconds));
            RecordMeasurementEvent(
                "indeks başlatma bitti",
                $"{startup.Stats.FileCount} dosya · {startup.Stats.DirectoryCount} klasör",
                stopwatch.ElapsedMilliseconds);
        } catch (OperationCanceledException) when (_isPreparedForShutdown) {
        } catch (Exception ex) {
            RecordMeasurementEvent(
                "indeks başlatma başarısız",
                ex.GetType().Name);
            Log($"❌ HATA: {ex.Message}");
            Log($"Stack trace: {ex.StackTrace}");
            await Dispatcher.InvokeAsync(() => {
                SetLoadingStatus($"Hata: {ex.Message}");
                SetLoadingIndeterminate(false);
                ModernDialog.Show(this, "İndeksleme başarısız",
                    $"{ex.Message}{Environment.NewLine}{Environment.NewLine}Ayrıntılar için konsolu kontrol edin.", DialogKind.Danger);
            });
        }
    }

    private void CompleteIndexInitialization(
        IndexStartupResult startup,
        long elapsedMilliseconds) {
        var stats = startup.Stats;
        SetLoadingIndeterminate(false);
        SetLoadingPercentage(100);
        SetLoadingStatus($"{stats.FileCount} dosya, {stats.DirectoryCount} klasör indekslendi");
        Log($"✅ İndeksleme tamamlandı ({elapsedMilliseconds}ms)");
        Log($"   📄 Dosya sayısı: {stats.FileCount}");
        Log($"   📁 Klasör sayısı: {stats.DirectoryCount}");
        Log($"   🔤 Token sayısı: {stats.TokenCount}");
        if (stats.LastScanTime.HasValue) {
            Log($"   🕐 Son tarama: {stats.LastScanTime.Value:g}");
        }

        Log("✅ Rule-based intent parser hazır");
        LoadDesktopIcons();
        _isIndexed = true;
        Log("✅ Arama motoru hazır");

        if (_indexLifecycle.ReconciliationStatus.IsRunning) {
            Log("Arka planda indeks uzlaştırması devam ediyor...");
            DeltaSyncPanel.Visibility = Visibility.Visible;
        } else {
            Log("FileSystemWatcher aktif - değişiklikler otomatik izleniyor");
        }

        var fromSkeleton = SkeletonPanel.Visibility == Visibility.Visible;
        LeaveCatalogBuildStage();
        HideLoadingOverlay();
        if (fromSkeleton && Animate) {
            DesktopIconsScroll.Visibility = Visibility.Visible;
            DesktopIconsScroll.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(320)) {
                EasingFunction = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut }
            });
        } else {
            ShowPanel(DesktopIconsScroll);
        }
        SearchBox.Focus();
    }

    private void HandleFileSystemChange(FileChangeEvent evt)
    {
        if (_isPreparedForShutdown ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished) return;

        Dispatcher.BeginInvoke(() => Log($"📁 {evt.ChangeType}: {System.IO.Path.GetFileName(evt.FullPath)}"));

        _fileChangeRefresh.Request();
        SchedulePendingFileChange();
    }

    private void SchedulePendingFileChange()
    {
        if (_isPreparedForShutdown ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished) return;

        lock (_fileChangeTimerLock)
        {
            if (_isPreparedForShutdown) return;

            _fileChangeDebounceTimer?.Dispose();
            _fileChangeDebounceTimer = new System.Threading.Timer(_ =>
            {
                if (_isPreparedForShutdown ||
                    Dispatcher.HasShutdownStarted ||
                    !_fileChangeRefresh.TryBegin()) return;

                _ = DispatchFileChangeAsync();
            }, null, FILE_CHANGE_DEBOUNCE_MS, Timeout.Infinite);
        }
    }

    private async Task DispatchFileChangeAsync()
    {
        try
        {
            await Dispatcher.InvokeAsync(ProcessFileChangeAsync)
                .Task
                .Unwrap();
        }
        catch (OperationCanceledException)
            when (_isPreparedForShutdown ||
                  Dispatcher.HasShutdownStarted)
        {
        }
        catch (InvalidOperationException)
            when (_isPreparedForShutdown ||
                  Dispatcher.HasShutdownStarted)
        {
        }
        catch (Exception ex)
        {
            Log($"⚠️ UI güncelleme kuyruğu hatası: {ex.Message}");
        }
        finally
        {
            if (_fileChangeRefresh.Complete())
            {
                SchedulePendingFileChange();
            }
        }
    }

    private async Task ProcessFileChangeAsync()
    {
        try
        {
            if (_currentFolderPath == null)
            {
                RefreshDesktopIconsSmart();
            }
            else
            {
                await RefreshCurrentFolderIconsAsync();
            }

            if (!string.IsNullOrWhiteSpace(SearchBox.Text) &&
                ResultsContainer.Visibility == Visibility.Visible)
            {
                await RefreshSearchResultsAsync();
            }
        }
        catch (OperationCanceledException)
            when (_lifetimeCancellation.IsCancellationRequested)
        {
        }
        catch (Exception ex)
        {
            Log($"⚠️ UI güncelleme hatası: {ex.Message}");
        }
    }
    
    private async Task RefreshCurrentFolderIconsAsync()
    {
        var folderPath = _currentFolderPath;
        if (string.IsNullOrEmpty(folderPath))
        {
            return;
        }

        try
        {
            var page = await _folderNavigation.OpenAsync(
                folderPath,
                MAX_FOLDER_ITEMS,
                ensureSynchronized: false,
                _lifetimeCancellation.Token);
            if (!string.Equals(
                    folderPath,
                    _currentFolderPath,
                    StringComparison.OrdinalIgnoreCase))
            {
                return;
            }

            var existing = _desktopIcons.ToDictionary(
                item => item.FullPath,
                item => item,
                StringComparer.OrdinalIgnoreCase);
            var desired = new List<DesktopIconViewModel>(page.Entries.Count);

            foreach (var entry in page.Entries)
            {
                if (!existing.TryGetValue(entry.FullPath, out var viewModel))
                {
                    viewModel = new DesktopIconViewModel();
                }

                viewModel.Name = entry.Name;
                viewModel.FullPath = entry.FullPath;
                viewModel.Icon = entry.IsDirectory
                    ? "folder"
                    : GetFileIcon(entry.Name);
                viewModel.IsDirectory = entry.IsDirectory;
                if (entry.IsDirectory)
                {
                    viewModel.SetFolderColors(entry.Name);
                }
                desired.Add(viewModel);
            }

            for (var index = 0; index < desired.Count; index++)
            {
                var currentIndex = _desktopIcons.IndexOf(desired[index]);
                if (currentIndex < 0)
                {
                    _desktopIcons.Insert(index, desired[index]);
                }
                else if (currentIndex != index)
                {
                    _desktopIcons.Move(currentIndex, index);
                }
            }

            while (_desktopIcons.Count > desired.Count)
            {
                _desktopIcons.RemoveAt(_desktopIcons.Count - 1);
            }

            if (desired.Count == 0)
            {
                var folderName = Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(folderName))
                {
                    folderName = folderPath;
                }

                EmptyFolderTitle.Text = $"'{folderName}' klasörü boş";
                EmptyFolderPanel.Visibility = Visibility.Visible;
            }
            else
            {
                EmptyFolderPanel.Visibility = Visibility.Collapsed;
            }
            RetargetThumbnailViewport(_desktopIcons.ToList());

            Log($"🔄 Klasör güncellendi: {_desktopIcons.Count} öğe" +
                (page.IsTruncated
                    ? $" (limit: {MAX_FOLDER_ITEMS})"
                    : string.Empty));
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"⚠️ Klasör güncelleme hatası: {ex.Message}");
        }
    }
    private void RefreshDesktopIconsSmart()
    {
        var indexedRoots = _indexLifecycle.GetIndexedRoots();
        
        var existingPaths = _desktopIcons.ToDictionary(d => d.FullPath, d => d, StringComparer.OrdinalIgnoreCase);
        var currentPaths = indexedRoots.Select(c => c.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        var toRemove = _desktopIcons.Where(d => !currentPaths.Contains(d.FullPath)).ToList();
        foreach (var item in toRemove)
        {
            _desktopIcons.Remove(item);
        }
        
        foreach (var child in indexedRoots.OrderBy(n => n.Name))
        {
            if (!existingPaths.ContainsKey(child.FullPath))
            {
                var viewModel = new DesktopIconViewModel
                {
                    Name = child.Name,
                    FullPath = child.FullPath,
                    Icon = child.IsDirectory ? "folder" : GetFileIcon(child.Name),
                    IsDirectory = child.IsDirectory
                };
                
                if (child.IsDirectory)
                {
                    viewModel.SetFolderColors(child.Name);
                }
                
                var insertIndex = _desktopIcons.TakeWhile(d => 
                {
                    if (d.IsDirectory == child.IsDirectory)
                    {
                        return string.Compare(d.Name, child.Name, StringComparison.OrdinalIgnoreCase) < 0;
                    }
                    return d.IsDirectory;
                }).Count();
                _desktopIcons.Insert(insertIndex, viewModel);
            }
        }

        RetargetThumbnailViewport(_desktopIcons.ToList());

        Log($"🔄 Desktop güncellendi: {_desktopIcons.Count} öğe");
    }
    
    private async Task RefreshSearchResultsAsync()
    {
        if (string.IsNullOrWhiteSpace(_lastSearchQuery)) return;
        
        try
        {
            var query = _lastSearchQuery;
            var version = Volatile.Read(ref _searchVersion);
            var cancellationToken =
                _currentSearchCancellation?.Token ?? CancellationToken.None;
            var outcome = await _searchService.SearchAsync(
                new SearchRequest(
                    query,
                    NaturalLanguageMode: false,
                    HasInternetConnection: _connectivityMonitor.IsConnected,
                    MaxResults: 50),
                cancellationToken);
            var results = outcome.Results;

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSearch(version)) return;
            
            await Dispatcher.InvokeAsync(() =>
            {
                if (!IsCurrentSearch(version)) return;

                CancelSearchThumbnailRequests();
        _searchResults.Clear();
                foreach (var result in results)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var isDirectory = result.IsDirectory;
                    
                    var viewModel = new SearchResultViewModel
                    {
                        Name = result.Name,
                        FullPath = result.FullPath,
                        Score = result.Score,
                        Icon = isDirectory ? "folder" : GetFileIcon(result.Name),
                        IsDirectory = isDirectory
                    };
                    
                    if (isDirectory) {
                        viewModel.SetFolderColors(result.Name);
                    }
                    
                    _searchResults.Add(viewModel);
                    _ = LoadSearchThumbnailAsync(viewModel);
                }
            });
            
            Log($"🔄 Arama sonuçları güncellendi");
        }
        catch (OperationCanceledException)
        {
        }
        catch (Exception ex)
        {
            Log($"⚠️ Arama güncelleme hatası: {ex.Message}");
        }
    }
    
    private void LoadDesktopIcons() {
        _desktopIcons.Clear();
        var indexedRoots = _indexLifecycle.GetIndexedRoots();

        Log($"📸 Thumbnail yükleme başladı... ({indexedRoots.Count} öğe)");

        var sortedChildren = indexedRoots
            .OrderBy(n => !n.IsDirectory)
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);

        var items = new List<DesktopIconViewModel>();
        foreach (var child in sortedChildren) {
            var viewModel = new DesktopIconViewModel {
                Name = child.Name,
                FullPath = child.FullPath,
                Icon = child.IsDirectory ? "folder" : GetFileIcon(child.Name),
                IsDirectory = child.IsDirectory
            };

            if (child.IsDirectory) {
                viewModel.SetFolderColors(child.Name);
            }

            _desktopIcons.Add(viewModel);
            items.Add(viewModel);
        }

        RetargetThumbnailViewport(items);
        RebuildDesktopRows();
        AnimateFolderSwap(items.Count);

        Log($"✅ Desktop ikonları yüklendi, küçük resimler görünür alana göre yüklenecek...");
    }
    
    private async Task LoadSearchThumbnailAsync(SearchResultViewModel viewModel)
    {
        if (viewModel.IsDirectory || !ThumbnailKinds.HasPreview(viewModel.Icon)) return;
        if (!_appSettings.ThumbnailPreviewsEnabled || _thumbnailActivity.IsIdle || _isPreparedForShutdown) return;
        var token = _searchThumbnailCancellation?.Token ?? _lifetimeCancellation.Token;
        try
        {
            var thumbnail = await _thumbnailService.GetThumbnailAsync(viewModel.FullPath, THUMBNAIL_SIZE, token);
            if (thumbnail == null || token.IsCancellationRequested) return;
            await Dispatcher.InvokeAsync(() =>
            {
                if (!token.IsCancellationRequested && CanApplyThumbnail(thumbnail) && _searchResults.Contains(viewModel))
                    viewModel.Thumbnail = thumbnail;
            });
        }
        catch (OperationCanceledException) { }
    }
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) {
        if (!_isIndexed) {
            Log("Arama yapılamadı: İndeksleme henüz tamamlanmadı");
            return;
        }
        
        var query = SearchBox.Text;
        
        if (string.IsNullOrWhiteSpace(query)) {
            CancelCurrentSearch();
            
            ResultsContainer.Visibility = Visibility.Collapsed;
            ShowPanel(DesktopIconsScroll);
            CancelSearchThumbnailRequests();
        _searchResults.Clear();
        } else {
            BeginSearch(query, debounce: true);
        }
    }

    private void BeginSearch(string query, bool debounce) {
        var cancellation = new CancellationTokenSource();
        var previous = Interlocked.Exchange(
            ref _currentSearchCancellation,
            cancellation);
        var version = Interlocked.Increment(ref _searchVersion);

        try {
            previous?.Cancel();
        } finally {
            previous?.Dispose();
        }

        _lastSearchQuery = query;
        _ = ExecuteSearchRequestAsync(query, version, cancellation, debounce);
    }

    private async Task ExecuteSearchRequestAsync(
        string query,
        long version,
        CancellationTokenSource cancellation,
        bool debounce) {
        try {
            if (debounce) {
                await Task.Delay(_isNaturalLanguageMode ? DEBOUNCE_DELAY_MS : LIVE_DEBOUNCE_MS, cancellation.Token);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentSearch(version)) return;

            DesktopIconsScroll.Visibility = Visibility.Collapsed;
            ShowPanel(ResultsContainer);

            if (_isNaturalLanguageMode) LogSearchQuery(query);
            if (_isNaturalLanguageMode ||
                (_searchResults.Count == 0 && NoResultsPanel.Visibility != Visibility.Visible)) {
                ShowSearchingIndicator(query);
            }

            await RunSearchAsync(query, version, cancellation.Token);
        } catch (OperationCanceledException) {
        } catch (Exception ex) {
            if (!IsCurrentSearch(version)) return;

            Log($"❌ Arama exception: {ex.Message}");
            ShowError("Arama sırasında bir hata oluştu", ex.Message);
        }
    }

    private void CancelCurrentSearch() {
        Interlocked.Increment(ref _searchVersion);
        var cancellation = Interlocked.Exchange(
            ref _currentSearchCancellation,
            null);

        try {
            cancellation?.Cancel();
        } finally {
            cancellation?.Dispose();
        }
    }

    private bool IsCurrentSearch(long version) =>
        version == Volatile.Read(ref _searchVersion);
    
    private void ShowSearchingIndicator(string query) {
        SearchingPanel.Visibility = Visibility.Visible;
        NoResultsPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Collapsed;
        ResultsGridScroll.Visibility = Visibility.Collapsed;
        
        FallbackWarningBanner.Visibility = Visibility.Collapsed;
        
        if (_isNaturalLanguageMode) {
            SearchingText.Text = "AI ile aranıyor…";
        } else {
            SearchingText.Text = "Aranıyor…";
        }
    }
    
    private void HideAllPanels() {
        SearchingPanel.Visibility = Visibility.Collapsed;
        NoResultsPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        FallbackWarningBanner.Visibility = Visibility.Collapsed;
    }
    
    private void ShowError(string title, string message) {
        SearchingPanel.Visibility = Visibility.Collapsed;
        NoResultsPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Collapsed;
        ResultsGridScroll.Visibility = Visibility.Collapsed;
        
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorTitle.Text = title;
        ErrorMessage.Text = message;
    }
    
    private void ShowFallbackWarning(string reason) {
        FallbackWarningBanner.Visibility = Visibility.Visible;
        FallbackReasonText.Text = reason;
        Log($"⚠️ AI fallback: {reason}");
    }
    
    private void CloseFallbackBanner_Click(object sender, RoutedEventArgs e) {
        FallbackWarningBanner.Visibility = Visibility.Collapsed;
    }
    
    private void CloseDeltaSyncBanner_Click(object sender, RoutedEventArgs e) {
        DeltaSyncWarningBanner.Visibility = Visibility.Collapsed;
    }
    
    private void ShowDeltaSyncWarning(string details) {
        DeltaSyncWarningBanner.Visibility = Visibility.Visible;
        DeltaSyncWarningText.Text = details;
    }
    
    private void UpdateDeltaSyncProgress(int processed, int total, int percentage) {
        DeltaSyncProgressBar.IsIndeterminate = false;
        DeltaSyncProgressBar.Value = percentage;
        DeltaSyncDetails.Text = $" - %{percentage}";
        DeltaSyncMinimizedText.Text = $"%{percentage}";
        
        if (percentage >= 100) {
            DeltaSyncPanel.Visibility = Visibility.Collapsed;
            DeltaSyncMinimized.Visibility = Visibility.Collapsed;
            Log("✅ Delta sync tamamlandı");
        }
    }

    private void UpdateDeltaSyncState(bool isRunning) {
        if (!isRunning) {
            DeltaSyncProgressBar.IsIndeterminate = false;
            DeltaSyncPanel.Visibility = Visibility.Collapsed;
            DeltaSyncMinimized.Visibility = Visibility.Collapsed;
            return;
        }

        DeltaSyncText.Text = "Değişiklikler kontrol ediliyor";
        DeltaSyncDetails.Text = string.Empty;
        DeltaSyncProgressBar.IsIndeterminate = true;
        DeltaSyncMinimizedText.Text = "Kontrol ediliyor";
        DeltaSyncPanel.Visibility = Visibility.Visible;
        DeltaSyncMinimized.Visibility = Visibility.Collapsed;
    }
    
    private void MinimizeDeltaSync_Click(object sender, RoutedEventArgs e) {
        DeltaSyncPanel.Visibility = Visibility.Collapsed;
        DeltaSyncMinimized.Visibility = Visibility.Visible;
    }
    
    private void ExpandDeltaSync_Click(object sender, MouseButtonEventArgs e) {
        DeltaSyncPanel.Visibility = Visibility.Visible;
        DeltaSyncMinimized.Visibility = Visibility.Collapsed;
    }
    
    private readonly DispatcherTimer _folderLoadingDelay = new() { Interval = TimeSpan.FromMilliseconds(120) };
    private int _navigationDirection;

    private long _loadingBarShownAt;
    private int _loadingBarHideToken;

    private void ShowFolderLoadingIndicator(string folderPath) {
        _folderLoadingDelay.Stop();
        _folderLoadingDelay.Start();
        if (DesktopIconsScroll.Visibility == Visibility.Visible && _desktopIcons.Count > 0) {
            DesktopIconsScroll.BeginAnimation(OpacityProperty, new System.Windows.Media.Animation.DoubleAnimation(0.55,
                TimeSpan.FromMilliseconds(SystemParameters.ClientAreaAnimation ? 120 : 0)));
        }
    }

    private void ShowFolderLoadingBarNow() {
        _folderLoadingDelay.Stop();
        Interlocked.Increment(ref _loadingBarHideToken);
        if (FolderLoadingBar.Visibility == Visibility.Visible) return;
        _loadingBarShownAt = Environment.TickCount64;
        FolderLoadingBar.IsIndeterminate = true;
        FolderLoadingBar.Visibility = Visibility.Visible;
    }

    private async void HideFolderLoadingIndicator() {
        _folderLoadingDelay.Stop();
        if (FolderLoadingBar.Visibility != Visibility.Visible) return;
        var token = Interlocked.Increment(ref _loadingBarHideToken);
        var shownFor = Environment.TickCount64 - _loadingBarShownAt;
        if (shownFor < 450) {
            try { await Task.Delay((int)(450 - shownFor)); } catch { return; }
            if (token != _loadingBarHideToken || _isPreparedForShutdown) return;
        }
        FolderLoadingBar.IsIndeterminate = false;
        FolderLoadingBar.Visibility = Visibility.Collapsed;
    }

    private void AnimatePanel(FrameworkElement panel, double fromX, double fromY, double fromOpacity) {
        if (!SystemParameters.ClientAreaAnimation) return;
        if (panel.RenderTransform is not System.Windows.Media.TranslateTransform shift) {
            shift = new System.Windows.Media.TranslateTransform();
            panel.RenderTransform = shift;
        }
        panel.CacheMode ??= new System.Windows.Media.BitmapCache();
        var duration = TimeSpan.FromMilliseconds(180);
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };
        var fade = new System.Windows.Media.Animation.DoubleAnimation(fromOpacity, 1, duration) { EasingFunction = ease };
        fade.Completed += (_, _) => panel.CacheMode = null;
        panel.BeginAnimation(OpacityProperty, fade);
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty,
            new System.Windows.Media.Animation.DoubleAnimation(fromX, 0, duration) { EasingFunction = ease });
        shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
            new System.Windows.Media.Animation.DoubleAnimation(fromY, 0, duration) { EasingFunction = ease });
    }

    private void AnimateFolderSwap(int incomingCount) {
        DesktopScroll?.ScrollToTop();
        var direction = _navigationDirection;
        _navigationDirection = 0;
        if (direction == 0 || incomingCount > 400 || incomingCount == 0) {
            DesktopIconsScroll.BeginAnimation(OpacityProperty, null);
            DesktopIconsScroll.Opacity = 1;
            if (direction != 0 && incomingCount == 0 && EmptyFolderPanel.Visibility == Visibility.Visible) {
                AnimatePanel(EmptyFolderPanel, direction * 12, 0, 0);
            }
            return;
        }
        AnimatePanel(DesktopIconsScroll, direction * 12, 0, 0.2);
    }
    
    private async void RetryButton_Click(object sender, RoutedEventArgs e) {
        if (!string.IsNullOrWhiteSpace(_lastSearchQuery)) {
            Log($"🔄 Yeniden deneniyor: '{_lastSearchQuery}'");
            try {
                await _connectivityMonitor.CheckNowAsync(
                    _lifetimeCancellation.Token);
            } catch (OperationCanceledException)
                when (_lifetimeCancellation.IsCancellationRequested) {
                return;
            }
            
            ErrorPanel.Visibility = Visibility.Collapsed;
            FallbackWarningBanner.Visibility = Visibility.Collapsed;
            BeginSearch(_lastSearchQuery, debounce: false);
        }
    }
    
    private async Task RunSearchAsync(
        string query,
        long searchVersion,
        CancellationToken cancellationToken = default) {
        cancellationToken.ThrowIfCancellationRequested();
        if (!IsCurrentSearch(searchVersion)) return;

        var reconciliation = _indexLifecycle.ReconciliationStatus;
        if (reconciliation.IsRunning) {
            ShowDeltaSyncWarning(
                $"%{reconciliation.Progress} tamamlandı ({reconciliation.Processed:N0}/{reconciliation.Total:N0} dosya kontrol edildi)");
        } else {
            DeltaSyncWarningBanner.Visibility = Visibility.Collapsed;
        }

        try {
            var searchTimestamp = Stopwatch.GetTimestamp();
            var reasoningEffort = _appSettings.AiReasoningEffort;
            if (_isNaturalLanguageMode) Log($"🤖 Arama eforu: {reasoningEffort}");
            var outcome = await _searchService.SearchAsync(
                new SearchRequest(
                    query,
                    _isNaturalLanguageMode,
                    _connectivityMonitor.IsConnected,
                    ReasoningEffort: reasoningEffort),
                cancellationToken);
            var searchElapsed = Stopwatch.GetElapsedTime(searchTimestamp);

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSearch(searchVersion)) return;

            RecordSearchMetrics(query.Length, searchElapsed, outcome.Results.Count);
            if (_isNaturalLanguageMode) {
                LogSearchOutcome(outcome);
            } else {
                _ = LogSettledSearchAsync(query, searchVersion, outcome, cancellationToken);
            }

            if (!string.IsNullOrEmpty(outcome.AutoOpenPath)) {
                cancellationToken.ThrowIfCancellationRequested();
                if (!IsCurrentSearch(searchVersion)) return;

                Log($"🚀 En iyi eşleşme açılıyor: {Path.GetFileName(outcome.AutoOpenPath)}");
                OpenFile(outcome.AutoOpenPath);
            }

            if (_isNaturalLanguageMode &&
                outcome.UsedFallback &&
                !string.IsNullOrEmpty(outcome.FallbackReason)) {
                ShowFallbackWarning(outcome.FallbackReason);
            } else if (_isNaturalLanguageMode &&
                       !string.IsNullOrEmpty(outcome.WarningMessage)) {
                ShowFallbackWarning(outcome.WarningMessage);
            } else {
                FallbackWarningBanner.Visibility = Visibility.Collapsed;
            }

            await Dispatcher.InvokeAsync(() => {
                if (cancellationToken.IsCancellationRequested || !IsCurrentSearch(searchVersion)) return;
                RenderSearchResults(outcome.Results, cancellationToken);
            }, System.Windows.Threading.DispatcherPriority.Background);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            if (!IsCurrentSearch(searchVersion)) return;

            Log($"❌ Arama hatası: {ex.Message}");
            Log($"Stack: {ex.StackTrace}");
            ShowError("Arama sırasında bir hata oluştu", ex.Message);
        }
    }

    private void LogSearchQuery(string query) {
        Log($"🔍 Arama sorgusu: '{query}'");
        var tokens = _searchDiagnostics.Tokenize(query);
        Log($"🔤 Query tokenler: [{string.Join(", ", tokens)}]");
    }

    private async Task LogSettledSearchAsync(string query, long version, SearchOutcome outcome, CancellationToken cancellationToken) {
        try {
            await Task.Delay(DEBOUNCE_DELAY_MS, cancellationToken);
        } catch (OperationCanceledException) {
            return;
        }
        if (!IsCurrentSearch(version) || _isPreparedForShutdown) return;
        LogSearchQuery(query);
        LogSearchOutcome(outcome);
    }

    private void LogSearchOutcome(SearchOutcome outcome) {
        if (outcome.Mode == SearchExecutionMode.OfflineFallback) {
            Log("⚠️ İnternet bağlantısı yok, standart aramaya geçildi");
        } else if (outcome.Mode == SearchExecutionMode.RuleBasedFallback) {
            Log($"⚠️ Groq API kullanılamadı, rule-based aramaya geçildi: {outcome.FallbackReason}");
        } else if (outcome.Mode == SearchExecutionMode.Advanced) {
            Log("🤖 Doğal dil sorgusu işlendi");
        }

        var structuredQuery = outcome.StructuredQuery;
        if (structuredQuery != null) {
            Log(outcome.UsedFallback
                ? "📋 Rule-based arama (fallback):"
                : "📋 AI destekli arama:");
            Log($"   Intent: {structuredQuery.Intent}");

            if (structuredQuery.FilterOnlyMode) {
                Log("   🔍 Mode: FILTER-ONLY (sadece filtrelerle arama)");
            } else {
                var primaryTerms = structuredQuery.SearchTerms
                    .Where(term =>
                        term.Role == SearchTermRole.Anchor &&
                        term.Category == SearchTermCategory.Exact)
                    .Select(term => term.Text);
                var alternativeTerms = structuredQuery.SearchTerms
                    .Where(term =>
                        term.Role == SearchTermRole.Anchor &&
                        term.Category != SearchTermCategory.Exact)
                    .Select(term => term.Text);
                var phraseTerms = structuredQuery.SearchTerms
                    .Where(term => term.Role == SearchTermRole.Phrase)
                    .Select(term => term.Text);
                var contextTerms = structuredQuery.SearchTerms
                    .Where(term => term.Role == SearchTermRole.Context)
                    .Select(term => term.Text);
                Log($"   Ana hedefler: [{string.Join(", ", primaryTerms)}]");
                if (structuredQuery.FolderContextTerms.Count > 0) {
                    Log($"   Klasör bağlamı: [{string.Join(", ", structuredQuery.FolderContextTerms.Select(term => term.Text))}]");
                }
                Log($"   Alternatifler: [{string.Join(", ", alternativeTerms)}]");
                Log($"   İfadeler: [{string.Join(", ", phraseTerms)}]");
                Log($"   Yardımcı bağlam: [{string.Join(", ", contextTerms)}]");
            }

            Log($"   File Types: [{string.Join(", ", structuredQuery.FileTypes)}]");
            if (structuredQuery.HardExtensions.Any()) {
                Log($"   Zorunlu uzantılar: [{string.Join(", ", structuredQuery.HardExtensions)}]");
            }
            if (structuredQuery.SoftExtensions.Any()) {
                Log($"   Önerilen uzantılar: [{string.Join(", ", structuredQuery.SoftExtensions)}]");
            }

            if (structuredQuery.DateFilter != null) {
                var dateFilter = structuredQuery.DateFilter;
                var parts = new List<string>();
                if (dateFilter.CreatedAfter != null) parts.Add($"Created > {dateFilter.CreatedAfter}");
                if (dateFilter.CreatedBeforeExclusive != null) parts.Add($"Created < {dateFilter.CreatedBeforeExclusive}");
                if (dateFilter.ModifiedAfter != null) parts.Add($"Modified > {dateFilter.ModifiedAfter}");
                if (dateFilter.ModifiedBeforeExclusive != null) parts.Add($"Modified < {dateFilter.ModifiedBeforeExclusive}");
                if (parts.Count > 0) {
                    Log($"   📅 Date Filter: {string.Join(", ", parts)}");
                }
            }

            if (structuredQuery.SizeFilter != null) {
                Log($"   Size Filter: {structuredQuery.SizeFilter.MinMb}MB - {structuredQuery.SizeFilter.MaxMb}MB");
            }
            if (structuredQuery.FolderHints.Any()) {
                Log($"   Folder Hints: [{string.Join(", ", structuredQuery.FolderHints.Select(hint => hint.Name))}]");
            }
            Log($"   Include Folders: {structuredQuery.IncludeFolderContents}");
        }

        Log($"✅ Sonuç sayısı: {outcome.Results.Count}");
        if (outcome.Results.Count > 0) {
            var topResults = outcome.Results
                .Take(5)
                .Select(result => $"   • {result.Name} (skor: {result.Score:F0})");
            Log($"🏆 İlk {Math.Min(5, outcome.Results.Count)} sonuç:");
            foreach (var result in topResults) {
                Log(result);
            }
            return;
        }

        Log("⚠️ Hiç sonuç bulunamadı!");
        foreach (var diagnostic in _searchDiagnostics.Inspect(
                     _lastSearchQuery,
                     _lifetimeCancellation.Token)) {
            Log($"   Token '{diagnostic.Token}' → indekste {diagnostic.MatchCount} eşleşme");
            if (diagnostic.SampleNames.Count > 0) {
                Log($"      Örnek: {string.Join(", ", diagnostic.SampleNames)}");
            }
        }
    }

    private void RenderSearchResults(
        IReadOnlyList<SearchResult> results,
        CancellationToken cancellationToken) {
        SearchingPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;

        if (results.Count == 0) {
            CancelSearchThumbnailRequests();
            foreach (var current in _searchResults) current.IsRemoving = false;
            _searchResults.Clear();
            NoResultsPanel.Visibility = Visibility.Visible;
            ResultsList.Visibility = Visibility.Collapsed;
            ResultsGridScroll.Visibility = Visibility.Collapsed;
            return;
        }

        NoResultsPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = _isGridViewMode
            ? Visibility.Collapsed
            : Visibility.Visible;
        ResultsGridScroll.Visibility = _isGridViewMode
            ? Visibility.Visible
            : Visibility.Collapsed;

        var existing = new Dictionary<string, SearchResultViewModel>(StringComparer.OrdinalIgnoreCase);
        foreach (var current in _searchResults) existing[current.FullPath] = current;

        var desired = new List<SearchResultViewModel>(results.Count);
        var added = new List<SearchResultViewModel>();
        foreach (var result in results) {
            cancellationToken.ThrowIfCancellationRequested();
            if (existing.TryGetValue(result.FullPath, out var kept)) {
                kept.Score = result.Score;
                desired.Add(kept);
                continue;
            }

            var isDirectory = result.IsDirectory;
            var viewModel = new SearchResultViewModel {
                Name = result.Name,
                FullPath = result.FullPath,
                Score = result.Score,
                Icon = isDirectory ? "folder" : GetFileIcon(result.Name),
                IsDirectory = isDirectory,
                IsNew = _searchResults.Count > 0 && SystemParameters.ClientAreaAnimation
            };

            if (isDirectory) {
                viewModel.SetFolderColors(result.Name);
            }

            desired.Add(viewModel);
            added.Add(viewModel);
        }

        var wanted = new HashSet<SearchResultViewModel>(desired);
        var animateRemoval = SystemParameters.ClientAreaAnimation;
        var leaving = new List<SearchResultViewModel>();
        for (var i = _searchResults.Count - 1; i >= 0; i--) {
            var current = _searchResults[i];
            if (wanted.Contains(current)) {
                current.IsRemoving = false;
                continue;
            }
            if (animateRemoval && !current.IsRemoving) {
                current.IsRemoving = true;
                leaving.Add(current);
            } else if (!animateRemoval) {
                _searchResults.RemoveAt(i);
            }
        }

        var host = ActiveResultsHost;
        var before = CaptureResultPositions(host, desired);

        var slot = 0;
        foreach (var item in desired) {
            while (slot < _searchResults.Count && _searchResults[slot].IsRemoving) slot++;
            var currentIndex = _searchResults.IndexOf(item);
            if (currentIndex < 0) {
                _searchResults.Insert(slot, item);
            } else if (currentIndex != slot) {
                _searchResults.Move(currentIndex, slot);
            }
            slot++;
        }

        AnimateResultTransitions(host, before, added);

        if (leaving.Count > 0) {
            var sweep = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(280) };
            sweep.Tick += (_, _) => {
                sweep.Stop();
                foreach (var gone in leaving) {
                    if (gone.IsRemoving) _searchResults.Remove(gone);
                }
            };
            sweep.Start();
        }

        foreach (var viewModel in added) {
            _ = LoadSearchResultThumbnailAsync(viewModel);
        }

        if (added.Count > 0 && added[0].IsNew) {
            var settle = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(450) };
            settle.Tick += (_, _) => {
                settle.Stop();
                foreach (var viewModel in added) viewModel.IsNew = false;
            };
            settle.Start();
        }
    }

    private Task LoadSearchResultThumbnailAsync(SearchResultViewModel viewModel) => LoadSearchThumbnailAsync(viewModel);

    private System.Windows.Controls.ItemsControl ActiveResultsHost => _isGridViewMode ? ResultsGrid : ResultsList;

    private static Dictionary<SearchResultViewModel, System.Windows.Point> CaptureResultPositions(
        System.Windows.Controls.ItemsControl host,
        IEnumerable<SearchResultViewModel> items) {
        var positions = new Dictionary<SearchResultViewModel, System.Windows.Point>();
        if (!SystemParameters.ClientAreaAnimation || !host.IsVisible) return positions;
        foreach (var item in items) {
            if (host.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container || !container.IsVisible) continue;
            try {
                positions[item] = container.TransformToAncestor(host).Transform(new System.Windows.Point(0, 0));
            } catch (InvalidOperationException) {
            }
        }
        return positions;
    }

    private void AnimateResultTransitions(
        System.Windows.Controls.ItemsControl host,
        Dictionary<SearchResultViewModel, System.Windows.Point> before,
        List<SearchResultViewModel> added) {
        if (!SystemParameters.ClientAreaAnimation || !host.IsVisible) return;
        host.UpdateLayout();
        var ease = new System.Windows.Media.Animation.CubicEase { EasingMode = System.Windows.Media.Animation.EasingMode.EaseOut };

        foreach (var (item, oldPosition) in before) {
            if (host.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container || !container.IsVisible) continue;
            System.Windows.Point newPosition;
            try {
                newPosition = container.TransformToAncestor(host).Transform(new System.Windows.Point(0, 0));
            } catch (InvalidOperationException) {
                continue;
            }
            var dx = oldPosition.X - newPosition.X;
            var dy = oldPosition.Y - newPosition.Y;
            if (Math.Abs(dx) < 0.5 && Math.Abs(dy) < 0.5) continue;

            var shift = new System.Windows.Media.TranslateTransform(dx, dy);
            container.RenderTransform = shift;
            var duration = TimeSpan.FromMilliseconds(240);
            var toZeroX = new System.Windows.Media.Animation.DoubleAnimation(0, duration) { EasingFunction = ease };
            toZeroX.Completed += (_, _) => {
                if (ReferenceEquals(container.RenderTransform, shift)) container.RenderTransform = null;
            };
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.XProperty, toZeroX);
            shift.BeginAnimation(System.Windows.Media.TranslateTransform.YProperty,
                new System.Windows.Media.Animation.DoubleAnimation(0, duration) { EasingFunction = ease });
        }

        var order = 0;
        foreach (var item in added) {
            if (host.ItemContainerGenerator.ContainerFromItem(item) is not FrameworkElement container) continue;
            var fade = new System.Windows.Media.Animation.DoubleAnimation(0, 1, TimeSpan.FromMilliseconds(260)) {
                BeginTime = TimeSpan.FromMilliseconds(Math.Min(order, 12) * 22),
                EasingFunction = ease
            };
            container.Opacity = 0;
            fade.Completed += (_, _) => { container.BeginAnimation(OpacityProperty, null); container.Opacity = 1; };
            container.BeginAnimation(OpacityProperty, fade);
            order++;
        }
    }

    private void SearchBox_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        if (e.Key == Key.Enter) {
            OpenSelected();
            e.Handled = true;
        } else if (e.Key == Key.Escape) {
            if (!string.IsNullOrWhiteSpace(SearchBox.Text)) {
                SearchBox.Clear();
                e.Handled = true;
            } else {
                SafeClose();
            }
        }
    }
    
    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e) {
        if (UiPreviewEnabled && e.Key == Key.F9) {
            CycleUiPreview();
            e.Handled = true;
            return;
        }

        if (e.Key == Key.Escape) {
            SafeClose();
            return;
        }
        
        bool ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool altPressed = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        
        var targetPath = _hoveredItemPath ?? _selectedItemPath;
        var targetItem = _hoveredItem;
        
        if (ctrlPressed && e.Key == Key.C && !string.IsNullOrEmpty(targetPath)) {
            CopyItemToClipboard(targetPath, isCut: false);
            e.Handled = true;
            return;
        }
        
        if (ctrlPressed && e.Key == Key.X && !string.IsNullOrEmpty(targetPath)) {
            CopyItemToClipboard(targetPath, isCut: true);
            if (targetItem != null) {
                if (_cutItem != null) {
                    _cutItem.IsCut = false;
                }
                targetItem.IsCut = true;
                _cutItem = targetItem;
            }
            e.Handled = true;
            return;
        }
        
        if (ctrlPressed && e.Key == Key.V) {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }
        
        if (ctrlPressed && shiftPressed && e.Key == Key.N) {
            ContextMenu_NewFolder(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        
        if (e.Key == Key.F2 && !string.IsNullOrEmpty(targetPath)) {
            RenameItem(targetPath);
            e.Handled = true;
            return;
        }
        
        if (e.Key == Key.Delete && !string.IsNullOrEmpty(targetPath)) {
            DeleteItem(targetPath);
            e.Handled = true;
            return;
        }
        
        if (e.Key == Key.F5) {
            RefreshCurrentFolder();
            ShowFeedback("🔄 Yenilendi");
            e.Handled = true;
            return;
        }
        
        if (altPressed && e.Key == Key.Enter && !string.IsNullOrEmpty(targetPath)) {
            _fileOperations.ShowProperties(targetPath);
            e.Handled = true;
            return;
        }
    }
    
    private void ShowFeedback(string message) {
        Log($"💬 {message}");
    }
    
    private void CopyItemToClipboard(string path, bool isCut) {
        _clipboardPath = path;
        _isCutOperation = isCut;
        
        if (!isCut && _cutItem != null) {
            _cutItem.IsCut = false;
            _cutItem = null;
        }
        
        var name = Path.GetFileName(path);
        var operation = isCut ? "✂️ Kesildi" : "📋 Kopyalandı";
        ShowFeedback($"{operation}: {name}");
    }
    
    private void PasteFromClipboard() {
        if (string.IsNullOrEmpty(_clipboardPath)) {
            ShowFeedback("⚠️ Panoda öğe yok");
            return;
        }

        var targetFolder = _currentFolderPath ??
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop);

        try {
            var result = _fileOperations.Paste(
                _clipboardPath,
                targetFolder,
                _isCutOperation);

            if (result.SourceKind == FileItemKind.Directory) {
                ShowFeedback(_isCutOperation
                    ? $"📁 Taşındı: {Path.GetFileName(result.DestinationPath)}"
                    : $"📁 Yapıştırıldı: {Path.GetFileName(result.DestinationPath)}");
            } else if (result.SourceKind == FileItemKind.File) {
                ShowFeedback(_isCutOperation
                    ? $"📄 Taşındı: {Path.GetFileName(result.DestinationPath)}"
                    : $"📄 Yapıştırıldı: {Path.GetFileName(result.DestinationPath)}");
            }

            if (_isCutOperation) {
                if (_cutItem != null) {
                    _desktopIcons.Remove(_cutItem);
                    _cutItem = null;
                }
                System.Windows.Clipboard.Clear();
                _clipboardPath = null;
                _isCutOperation = false;
            }

            RefreshCurrentFolder();
        } catch (Exception ex) {
            ShowFeedback($"❌ Yapıştırma hatası: {ex.Message}");
        }
    }

    private void RenameItem(string path) {
        var currentName = Path.GetFileName(path);
        var dialog = new RenameDialog(currentName);
        dialog.Owner = this;
        
        if (dialog.ShowDialog() == true) {
            var newName = dialog.NewName;
            
            try {
                var operation = _fileOperations.Rename(path, newName);
                if (operation.SourceKind == FileItemKind.Directory) {
                    ShowFeedback($"📁 Adlandırıldı: {currentName} → {newName}");
                } else if (operation.SourceKind == FileItemKind.File) {
                    ShowFeedback($"📄 Adlandırıldı: {currentName} → {newName}");
                }
                UpdateItemInView(
                    path,
                    operation.DestinationPath,
                    newName);
            } catch (Exception ex) {
                ShowFeedback($"❌ Adlandırma hatası: {ex.Message}");
            }
        }
    }
    
    private void DeleteItem(string path) {
        var name = Path.GetFileName(path);
        var confirmed = ModernDialog.Confirm(this, $"'{name}' silinsin mi?",
            "Öğe Geri Dönüşüm Kutusu'na taşınacak, oradan geri alabilirsiniz.", "Sil", DialogKind.Danger);
        
        if (confirmed) {
            try {
                var itemKind = _fileOperations.DeleteToRecycleBin(path);
                if (itemKind != FileItemKind.Missing) {
                    ShowFeedback($"🗑️ Silindi: {name}");
                }
                RemoveItemFromView(path);
            } catch (Exception ex) {
                ShowFeedback($"❌ Silme hatası: {ex.Message}");
            }
        }
    }
    
    private void OpenSelected() {
        if (ResultsList.SelectedItem is SearchResultViewModel srvm) {
            OpenFile(srvm.FullPath);
        }
    }
    
    private void DesktopIcon_Click(object sender, RoutedEventArgs e) {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string path) {
            OpenFile(path);
        }
    }
    
    private void GridItem_MouseLeftButtonDown(object sender, MouseButtonEventArgs e) {
        if (sender is Border border && border.DataContext is SearchResultViewModel srvm) {
            OpenFile(srvm.FullPath);
        }
    }
    
    private void GridItem_Click(object sender, RoutedEventArgs e) {
        if (sender is System.Windows.Controls.Button btn && btn.Tag is string path) {
            OpenFile(path);
        }
    }
    
    private void OpenFile(string path) {
        try {
            if (_fileOperations.GetItemKind(path) == FileItemKind.Directory) {
                _ = OpenFolderInApp(path);
            } else {
                _fileOperations.OpenFile(path);
            }
        } catch (Exception ex) {
            ModernDialog.Show(this, "Açılamadı", ex.Message, DialogKind.Warning);
        }
    }
    
    private async Task OpenFolderInApp(string folderPath) {
        try {
            if (_navigationDirection == 0) _navigationDirection = 1;
            Log($"📂 Klasör açılıyor: {folderPath}");
            
            ShowFolderLoadingIndicator(folderPath);
            
            try {
                if (!await LoadFolderContentsAsync(
                        folderPath,
                        ensureSynchronized: true)) {
                    return;
                }

                _currentFolderPath = folderPath;
                
                SearchBox.Clear();
                ResultsContainer.Visibility = Visibility.Collapsed;
                ShowPanel(DesktopIconsScroll);
                
                BackButton.Visibility = Visibility.Visible;
                
                var folderName = System.IO.Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(folderName)) folderName = folderPath;
                SearchWatermark.Text = $"📂 {folderName}";
            } finally {
                HideFolderLoadingIndicator();
            }
            
        } catch (Exception ex) {
            Log($"❌ Klasör açılamadı: {ex.Message}");
            ModernDialog.Show(this, "Klasör açılamadı", ex.Message, DialogKind.Warning);
        }
    }
    
    private const int MAX_FOLDER_ITEMS = 1000;

    private static readonly bool UiPreviewEnabled =
        !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("OMNISPOT_UI_ONIZLEME"));
    private int _uiPreviewScene;

    private void CycleUiPreview() {
        _uiPreviewScene = (_uiPreviewScene + 1) % 6;
        CancelCurrentSearch();
        HideAllPanels();
        DeltaSyncWarningBanner.Visibility = Visibility.Collapsed;
        DeltaSyncPanel.Visibility = Visibility.Collapsed;
        DeltaSyncMinimized.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Collapsed;
        ResultsGridScroll.Visibility = Visibility.Collapsed;
        EmptyFolderPanel.Visibility = Visibility.Collapsed;

        switch (_uiPreviewScene) {
            case 1:
                DesktopIconsScroll.Visibility = Visibility.Collapsed;
                ShowPanel(ResultsContainer);
                ShowSearchingIndicator("önizleme");
                ShowFallbackWarning("Örnek: AI hizmetine ulaşılamadı, zaman aşımı (8 sn)");
                break;
            case 2:
                DesktopIconsScroll.Visibility = Visibility.Collapsed;
                ShowPanel(ResultsContainer);
                NoResultsPanel.Visibility = Visibility.Visible;
                NoResultsHint.Text = "Farklı anahtar kelimeler deneyin";
                ShowDeltaSyncWarning("Daha iyi sonuçlar için birkaç saniye bekleyin");
                break;
            case 3:
                DesktopIconsScroll.Visibility = Visibility.Collapsed;
                ShowPanel(ResultsContainer);
                ShowError("Bağlantı hatası", "Örnek: AI hizmetine bağlanılamadı. İnternet bağlantınızı kontrol edip tekrar deneyin.");
                UpdateDeltaSyncState(true);
                UpdateDeltaSyncProgress(420, 1000, 42);
                break;
            case 4:
                ResultsContainer.Visibility = Visibility.Collapsed;
                ShowPanel(DesktopIconsScroll);
                EmptyFolderTitle.Text = "'Önizleme' klasörü boş";
                EmptyFolderPanel.Visibility = Visibility.Visible;
                UpdateDeltaSyncState(true);
                MinimizeDeltaSync_Click(this, new RoutedEventArgs());
                DeltaSyncMinimizedText.Text = "%42";
                break;
            case 5:
                ResultsContainer.Visibility = Visibility.Collapsed;
                ShowPanel(DesktopIconsScroll);
                UpdateDeltaSyncState(true);
                break;
            default:
                ResultsContainer.Visibility = Visibility.Collapsed;
                ShowPanel(DesktopIconsScroll);
                break;
        }

        Log($"🎨 UI önizleme sahnesi {_uiPreviewScene}/5");
    }

    private static readonly int FolderOpenDebugDelayMs =
        int.TryParse(Environment.GetEnvironmentVariable("OMNISPOT_KLASOR_GECIKME_MS"), out var ms) && ms > 0 ? ms : 0;
    
    private const int THUMBNAIL_BATCH_SIZE = 20;

    private const int THUMBNAIL_PREFETCH_SCREENS = 1;


    private const int VIEWPORT_DEBOUNCE_MS = 100;
    
    private async Task<bool> LoadFolderContentsAsync(
        string folderPath,
        bool ensureSynchronized = false) {
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(
            _lifetimeCancellation.Token);
        var previous = Interlocked.Exchange(
            ref _folderLoadCancellation,
            cancellation);
        try {
            previous?.Cancel();
        } finally {
            previous?.Dispose();
        }

        try {
            var page = await _folderNavigation.OpenAsync(
                folderPath,
                MAX_FOLDER_ITEMS,
                ensureSynchronized,
                cancellation.Token);
            if (FolderOpenDebugDelayMs > 0) {
                await Task.Delay(FolderOpenDebugDelayMs, cancellation.Token);
            }
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_folderLoadCancellation, cancellation)) {
                return false;
            }

            _thumbnailViewport?.Cancel();
            _desktopIcons.Clear();
            EmptyFolderPanel.Visibility = Visibility.Collapsed;

            var items = page.Entries.Select(entry => {
                var viewModel = new DesktopIconViewModel {
                    Name = entry.Name,
                    FullPath = entry.FullPath,
                    Icon = entry.IsDirectory ? "folder" : GetFileIcon(entry.Name),
                    IsDirectory = entry.IsDirectory
                };

                if (entry.IsDirectory) {
                    viewModel.SetFolderColors(entry.Name);
                }

                return viewModel;
            }).ToList();

            RetargetThumbnailViewport(items);

            if (items.Count == 0) {
                var folderName = Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(folderName)) folderName = folderPath;

                EmptyFolderTitle.Text = $"'{folderName}' klasörü boş";
                EmptyFolderPanel.Visibility = Visibility.Visible;
                Log("   📂 Klasör boş");
                RecordFolderMetrics(folderPath, 0, page.IsTruncated);
            } else {
                foreach (var item in items) {
                    _desktopIcons.Add(item);
                }

                Log($"   📊 {_desktopIcons.Count} öğe yüklendi" +
                    (page.IsTruncated ? $" (limit: {MAX_FOLDER_ITEMS})" : string.Empty));
                RecordFolderMetrics(folderPath, items.Count, page.IsTruncated);
            }

            RebuildDesktopRows();
            AnimateFolderSwap(items.Count);

            return true;
        } catch (OperationCanceledException) {
            return false;
        } catch (Exception ex) {
            Log($"❌ Klasör içeriği yüklenemedi: {ex.Message}");
            return false;
        } finally {
            Interlocked.CompareExchange(
                ref _folderLoadCancellation,
                null,
                cancellation);
            cancellation.Dispose();
        }
    }

    private void InitializeThumbnailViewport() {
        _thumbnailViewport = new ThumbnailViewportScheduler(
            _thumbnailService,
            (icon, thumbnail, token) => {
                return Dispatcher.InvokeAsync(() => {
                    if (!token.IsCancellationRequested && CanApplyThumbnail(thumbnail)
                        && _desktopIcons.Contains(icon)) icon.Thumbnail = thumbnail;
                }, System.Windows.Threading.DispatcherPriority.Background).Task;
            },
            THUMBNAIL_SIZE,
            THUMBNAIL_BATCH_SIZE,
            THUMBNAIL_PREFETCH_SCREENS);

        _viewportDebounce.Interval =
            TimeSpan.FromMilliseconds(VIEWPORT_DEBOUNCE_MS);
        _viewportDebounce.Tick += (_, __) => {
            _viewportDebounce.Stop();
            UpdateThumbnailViewport();
        };

        DesktopIcons.AddHandler(ScrollViewer.ScrollChangedEvent, new ScrollChangedEventHandler((_, __) => ScheduleViewportUpdate()));
        DesktopIconsScroll.SizeChanged += (_, __) => ScheduleViewportUpdate();
        DesktopIconsScroll.IsVisibleChanged += (_, __) => ScheduleViewportUpdate();

        InitializeThumbnailPolicy();
    }

    private void RetargetThumbnailViewport(IReadOnlyList<DesktopIconViewModel> items) {
        _thumbnailViewport?.Reset(items);
        ScheduleViewportUpdate();
    }

    private void ScheduleViewportUpdate() {
        if (_isPreparedForShutdown) return;
        _viewportDebounce.Stop();
        _viewportDebounce.Start();
    }

    private void UpdateThumbnailViewport() {
        if (_isPreparedForShutdown) return;
        if (!_appSettings.ThumbnailPreviewsEnabled || _thumbnailActivity.IsIdle) return;
        _thumbnailViewport?.Update(ComputeThumbnailViewport());
    }

    private ThumbnailViewport ComputeThumbnailViewport() {
        if (DesktopIconsScroll.Visibility != Visibility.Visible) return default;

        var count = _desktopIcons.Count;
        if (count == 0 || _desktopRows.Count == 0) return default;

        FrameworkElement? container = null;
        for (var i = 0; i < _desktopRows.Count && container == null; i++) {
            container = DesktopIcons.ItemContainerGenerator.ContainerFromIndex(i) as FrameworkElement;
        }
        var scroll = DesktopScroll;
        if (container == null || scroll == null) return default;

        var rowHeight = container.ActualHeight + container.Margin.Top + container.Margin.Bottom;
        var viewportHeight = scroll.ViewportHeight;
        if (rowHeight <= 0 || viewportHeight <= 0) return default;

        var columns = DesktopColumns;
        var firstRow = Math.Max(0, (int)(scroll.VerticalOffset / rowHeight));
        var rows = (int)Math.Ceiling(viewportHeight / rowHeight) + 1;

        var first = Math.Min(count, firstRow * columns);
        var visible = Math.Min(count - first, rows * columns);
        return visible <= 0 ? default : new ThumbnailViewport(first, visible);
    }
    
    private void LoadFolderContents(string folderPath) {
        _ = LoadFolderContentsAsync(folderPath);
    }
    
    private async Task LoadFolderThumbnailAsync(DesktopIconViewModel icon) {
        try {
            if (!_appSettings.ThumbnailPreviewsEnabled || _thumbnailActivity.IsIdle) return;
            var thumbnail = await _thumbnailService.GetThumbnailAsync(icon.FullPath, THUMBNAIL_SIZE, _lifetimeCancellation.Token);
            if (thumbnail != null) {
                await Dispatcher.InvokeAsync(() => {
                    if (CanApplyThumbnail(thumbnail) && _desktopIcons.Contains(icon)) icon.Thumbnail = thumbnail;
                });
            }
        } catch { }
    }
    
    private string GetFileIcon(string filenameOrExtension) {
        var ext = filenameOrExtension.StartsWith(".") 
            ? filenameOrExtension.ToLowerInvariant() 
            : System.IO.Path.GetExtension(filenameOrExtension).ToLowerInvariant();
            
        return ext switch {
            ".pdf" => "pdf",
            ".doc" or ".docx" or ".odt" or ".rtf" => "doc",
            ".xls" or ".xlsx" or ".csv" or ".ods" => "sheet",
            ".ppt" or ".pptx" or ".odp" => "slides",
            ".txt" or ".md" or ".log" or ".json" or ".xml" => "text",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" or ".svg" or ".heic" => "image",
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" or ".ogg" => "audio",
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" or ".webm" => "video",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "archive",
            ".exe" or ".msi" or ".bat" or ".cmd" => "app",
            ".lnk" or ".url" => "link",
            ".html" or ".htm" => "web",
            ".cs" or ".js" or ".ts" or ".py" or ".java" or ".cpp" or ".c" or ".h" or ".php" or ".xaml" or ".ps1" => "code",
            _ => "file"
        };
    }
    
    private void GoToParentFolder() {
        if (string.IsNullOrEmpty(_currentFolderPath)) {
            GoToHome();
            return;
        }

        var parent = _folderNavigation.GetParentWithinRoots(
            _currentFolderPath,
            _indexedRootPaths);
        if (string.IsNullOrEmpty(parent) ||
            _fileOperations.GetItemKind(parent) != FileItemKind.Directory) {
            GoToHome();
            return;
        }

        _navigationDirection = -1;
        _ = OpenFolderInApp(parent);
    }
    private void GoToHome() {
        _currentFolderPath = null;
        BackButton.Visibility = Visibility.Collapsed;
        _navigationDirection = -1;
        LoadDesktopIcons();
        SearchWatermark.Text = "OmniSpot: Hafif Basit Masaüstü ve Tarayıcı";
    }
    
    private void BackButton_Click(object sender, RoutedEventArgs e) {
        GoToParentFolder();
    }
    
    private void SettingsButton_Click(object sender, RoutedEventArgs e) {
        OpenSettings();
    }
    
    private void CloseButton_Click(object sender, RoutedEventArgs e) {
        SafeClose();
    }
    
    private void SafeClose() {
        if (_appSettings.MinimizeToTrayOnClose) {
            MinimizeToTray();
        } else {
            ForceExit();
        }
    }
    
    #region File Operations & Context Menu
    
    private void FileItem_MouseEnter(object sender, System.Windows.Input.MouseEventArgs e) {
        if (sender is FrameworkElement element) {
            if (element.DataContext is DesktopIconViewModel divm) {
                _hoveredItemPath = divm.FullPath;
                _hoveredItem = divm;
            } else if (element.Tag is string path) {
                _hoveredItemPath = path;
                _hoveredItem = _desktopIcons.FirstOrDefault(i => 
                    string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
            }
        }
    }
    
    private void FileItem_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) {
        _hoveredItemPath = null;
        _hoveredItem = null;
    }
    
    private void FileItem_RightClick(object sender, MouseButtonEventArgs e) {
        if (sender is FrameworkElement element) {
            if (element.Tag is string path) {
                _selectedItemPath = path;
            } else if (element.DataContext is DesktopIconViewModel divm) {
                _selectedItemPath = divm.FullPath;
            } else if (element.DataContext is SearchResultViewModel srvm) {
                _selectedItemPath = srvm.FullPath;
            }
            Log($"📌 Seçilen öğe: {_selectedItemPath}");
        }
    }
    
    private void EmptyArea_RightClick(object sender, MouseButtonEventArgs e) {
        if (e.OriginalSource is ScrollViewer || e.OriginalSource is Grid) {
            _selectedItemPath = _currentFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        }
    }
    
    private void ContextMenu_Open(object sender, RoutedEventArgs e) {
        var path = GetPathFromContextMenu(sender);
        if (string.IsNullOrEmpty(path)) return;
        OpenFile(path);
    }
    
    private void ContextMenu_OpenWith(object sender, RoutedEventArgs e) {
        var path = GetPathFromContextMenu(sender);
        if (string.IsNullOrEmpty(path)) return;

        try {
            _fileOperations.OpenWith(path);
            Log($"🔗 Birlikte aç: {path}");
        } catch (Exception ex) {
            Log($"❌ Birlikte aç hatası: {ex.Message}");
            ModernDialog.Show(this, "Birlikte aç başarısız", ex.Message, DialogKind.Danger);
        }
    }

    private void ContextMenu_Copy(object sender, RoutedEventArgs e) {
        var path = GetPathFromContextMenu(sender);
        if (string.IsNullOrEmpty(path)) return;
        CopyItemToClipboard(path, isCut: false);
    }
    
    private void ContextMenu_Cut(object sender, RoutedEventArgs e) {
        var path = GetPathFromContextMenu(sender);
        if (string.IsNullOrEmpty(path)) return;
        
        var item = _desktopIcons.FirstOrDefault(i => 
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        
        CopyItemToClipboard(path, isCut: true);
        
        if (item != null) {
            if (_cutItem != null) {
                _cutItem.IsCut = false;
            }
            item.IsCut = true;
            _cutItem = item;
        }
    }
    
    private void ContextMenu_Paste(object sender, RoutedEventArgs e) {
        PasteFromClipboard();
    }
    
    private void ContextMenu_Rename(object sender, RoutedEventArgs e) {
        var path = GetPathFromContextMenu(sender);
        if (string.IsNullOrEmpty(path)) return;
        RenameItem(path);
    }
    
    private void ContextMenu_Delete(object sender, RoutedEventArgs e) {
        var path = GetPathFromContextMenu(sender);
        if (string.IsNullOrEmpty(path)) return;
        DeleteItem(path);
    }
    
    private void ContextMenu_OpenLocation(object sender, RoutedEventArgs e) {
        var path = GetPathFromContextMenu(sender);
        if (string.IsNullOrEmpty(path)) return;

        try {
            var directory = _fileOperations.GetItemKind(path) ==
                            FileItemKind.Directory
                ? path
                : Path.GetDirectoryName(path);

            if (!string.IsNullOrEmpty(directory)) {
                _fileOperations.Reveal(path);
                Log($"📍 Konum açıldı: {directory}");
            }
        } catch (Exception ex) {
            Log($"❌ Konum açma hatası: {ex.Message}");
        }
    }

    private void ContextMenu_Properties(object sender, RoutedEventArgs e) {
        var path = GetPathFromContextMenu(sender);
        if (string.IsNullOrEmpty(path)) return;

        try {
            _fileOperations.ShowProperties(path);
            Log($"ℹ️ Özellikler açıldı: {Path.GetFileName(path)}");
        } catch (Exception ex) {
            Log($"❌ Özellikler açma hatası: {ex.Message}");
        }
    }

    private string? GetPathFromContextMenu(object sender) {
        if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu) {
            if (contextMenu.PlacementTarget is FrameworkElement element) {
                if (element.Tag is string tagPath) {
                    return tagPath;
                }
                if (element is System.Windows.Controls.ListViewItem listViewItem && listViewItem.Content is SearchResultViewModel srvm) {
                    return srvm.FullPath;
                }
                if (element.DataContext is SearchResultViewModel srvm2) {
                    return srvm2.FullPath;
                }
                if (element.DataContext is DesktopIconViewModel divm) {
                    return divm.FullPath;
                }
            }
        }
        
        return _selectedItemPath;
    }
    
    private void ContextMenu_NewFolder(object sender, RoutedEventArgs e) {
        var targetFolder = _currentFolderPath ??
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var dialog = new RenameDialog("Yeni Klasör", isNew: true) {
            Owner = this
        };

        if (dialog.ShowDialog() != true) return;

        try {
            _fileOperations.CreateFolder(targetFolder, dialog.NewName);
            Log($"📁 Yeni klasör oluşturuldu: {dialog.NewName}");
            RefreshCurrentFolder();
        } catch (Exception ex) {
            Log($"❌ Klasör oluşturma hatası: {ex.Message}");
            ModernDialog.Show(this, "Klasör oluşturulamadı", ex.Message, DialogKind.Danger);
        }
    }

    private void ContextMenu_NewTextFile(object sender, RoutedEventArgs e) {
        var targetFolder = _currentFolderPath ??
            Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var dialog = new RenameDialog("Yeni Metin Belgesi.txt", isNew: true) {
            Owner = this
        };

        if (dialog.ShowDialog() != true) return;

        try {
            _fileOperations.CreateTextFile(targetFolder, dialog.NewName);
            Log($"📄 Yeni dosya oluşturuldu: {dialog.NewName}");
            RefreshCurrentFolder();
        } catch (Exception ex) {
            Log($"❌ Dosya oluşturma hatası: {ex.Message}");
            ModernDialog.Show(this, "Dosya oluşturulamadı", ex.Message, DialogKind.Danger);
        }
    }

    private void ContextMenu_Refresh(object sender, RoutedEventArgs e) {
        RefreshCurrentFolder();
    }
    
    private void RefreshCurrentFolder() {
        if (_currentFolderPath != null) {
            LoadFolderContents(_currentFolderPath);
        } else {
            LoadDesktopIcons();
        }
    }
    
    private void RefreshFolderContaining(string itemPath) {
        var parentFolder = Path.GetDirectoryName(itemPath);
        if (string.IsNullOrEmpty(parentFolder)) {
            RefreshCurrentFolder();
            return;
        }
        
        if (_currentFolderPath != null && 
            string.Equals(parentFolder.TrimEnd('\\', '/'), _currentFolderPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) {
            LoadFolderContents(_currentFolderPath);
        }
        else if (_currentFolderPath == null) {
            LoadDesktopIcons();
        }
        else {
            RefreshCurrentFolder();
        }
    }
    
    private void UpdateItemInView(string oldPath, string newPath, string newName) {
        var icon = _desktopIcons.FirstOrDefault(i => 
            string.Equals(i.FullPath, oldPath, StringComparison.OrdinalIgnoreCase));
        
        if (icon != null) {
            icon.Name = newName;
            icon.FullPath = newPath;
            Log($"✅ Görünüm güncellendi: {newName}");
        } else {
            RefreshCurrentFolder();
        }
    }
    
    private void RemoveItemFromView(string path) {
        var icon = _desktopIcons.FirstOrDefault(i => 
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        
        if (icon != null) {
            _desktopIcons.Remove(icon);
            Log($"✅ Görünümden kaldırıldı: {Path.GetFileName(path)}");
        }
    }
    
    #endregion
}
