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
    private System.Threading.Timer? _fileChangeDebounceTimer; // Dosya değişikliği debounce
    private readonly object _fileChangeTimerLock = new();
    private readonly RefreshCoalescer _fileChangeRefresh = new();
    private const int FILE_CHANGE_DEBOUNCE_MS = 1000; // 1 saniye debounce (daha az kasma için artırıldı)
    private CancellationTokenSource? _currentSearchCancellation;
    private CancellationTokenSource? _folderLoadCancellation;
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
    private const int DEBOUNCE_DELAY_MS = 1200; // 1.2 seconds delay after last keystroke (increased from 400ms)
    private const int THUMBNAIL_SIZE = 128; // Thumbnail boyutu
    
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
        ApplicationLog applicationLog) {
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

        DataContext = _viewModel;
        InitializeComponent();

        _indexLifecycle.ProgressChanged += HandleIndexProgress;
        _indexLifecycle.Error += HandleIndexError;
        _indexLifecycle.FileChanged += HandleFileSystemChange;
        _indexLifecycle.ReconciliationProgressChanged += HandleReconciliationProgress;
        _indexLifecycle.ReconciliationStateChanged += HandleReconciliationStateChanged;
        _shellService.ToggleRequested += HandleShellToggleRequested;
        _shellService.ShowRequested += HandleShellShowRequested;
        _shellService.SettingsRequested += HandleShellSettingsRequested;
        _shellService.ExitRequested += HandleShellExitRequested;
        SourceInitialized += HandleSourceInitialized;
        
        // Wire up events
        SearchBox.TextChanged += SearchBox_TextChanged;
        SearchBox.GotFocus += (_, __) => SearchWatermark.Visibility = Visibility.Collapsed;
        SearchBox.LostFocus += (_, __) => {
            if (string.IsNullOrWhiteSpace(SearchBox.Text)) 
                SearchWatermark.Visibility = Visibility.Visible;
        };
        SearchBox.KeyDown += SearchBox_KeyDown;
        ResultsList.MouseDoubleClick += (_, __) => OpenSelected();
        ConsoleToggleButton.Click += (_, __) => ToggleDiagnosticsWindow();
        InitializeDiagnostics();
        NaturalLanguageToggle.Checked += (_, __) => EnableNaturalLanguageMode();
        NaturalLanguageToggle.Unchecked += (_, __) => DisableNaturalLanguageMode();
        ViewModeToggle.Checked += (_, __) => EnableGridView();
        ViewModeToggle.Unchecked += (_, __) => DisableGridView();
        
        Log("=== OmniSpot Başlatıldı ===");
        Log("OmniSpot: Hafif Basit Masaüstü ve Tarayıcı");
        
        // Pencere kapatma olayını yakala
        Closing += MainWindow_Closing;
        
        // Ayarlardan varsayılan modları uygula
        ApplyDefaultSettings();
        
        // Start async indexing after window loads
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
    
    /// <summary>
    /// Varsayılan ayarları uygular
    /// </summary>
    private void ApplyDefaultSettings() {
        if (_appSettings.NaturalLanguageModeEnabled) {
            NaturalLanguageToggle.IsChecked = true;
        }
        if (_appSettings.GridViewEnabled) {
            ViewModeToggle.IsChecked = true;
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
    /// <summary>
    /// Pencereyi gösterir ve aktif yapar
    /// </summary>
    private void ShowAndActivate() {
        Show();
        WindowState = WindowState.Normal;
        Activate();
        SearchBox.Focus();
        SearchBox.SelectAll();
        Log("🔔 OmniSpot uyandırıldı");
    }
    
    /// <summary>
    /// Pencereyi system tray'e küçültür
    /// </summary>
    private void MinimizeToTray() {
        if (_appSettings.MinimizeToTrayOnClose) {
            Hide();
        } else {
            WindowState = WindowState.Minimized;
        }
    }
    
    /// <summary>
    /// Pencere kapatılmaya çalışıldığında
    /// </summary>
    private void MainWindow_Closing(object? sender, System.ComponentModel.CancelEventArgs e) {
        if (_appSettings.MinimizeToTrayOnClose) {
            e.Cancel = true;
            MinimizeToTray();
            Log("📌 OmniSpot system tray'e küçültüldü");
        }
    }
    
    /// <summary>
    /// Ayarlar penceresini açar
    /// </summary>
    private void OpenSettings() {
        // Önce mevcut hotkey'i kaldır (ayarlar değişebilir)
        _shellService.SuspendHotkey();
        
        var settingsWindow = new SettingsWindow(
            _appSettings,
            _settingsApplication,
            _indexMaintenance,
            Log);
        settingsWindow.Owner = this;
        settingsWindow.SettingsChanged += OnSettingsChanged;
        settingsWindow.IndexRebuildRequested += OnIndexRebuildRequested;
        settingsWindow.ShowDialog();
        
        // Hotkey'i yeniden kaydet (değişmiş olabilir)
        if (!_isPreparedForShutdown) {
            _shellService.ApplyHotkey(_appSettings);
        }
    }
    
    /// <summary>
    /// Ayarlar değiştiğinde çağrılır
    /// </summary>
    private void OnSettingsChanged(object? sender, AppSettings newSettings) {
        _appSettings = newSettings;
        Log("⚙️ Ayarlar güncellendi");
    }

    private void OnIndexRebuildRequested(object? sender, EventArgs e) {
        ForceExit();
    }
    
    /// <summary>
    /// Uygulamadan tamamen çıkış yapar
    /// </summary>
    private void ForceExit() {
        PrepareForShutdown();
        System.Windows.Application.Current.Shutdown();
    }

    internal void PrepareForShutdown() {
        if (_isPreparedForShutdown) return;
        _isPreparedForShutdown = true;

        _lifetimeCancellation.Cancel();
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
    
    /// <summary>
    /// AI butonunun durumunu günceller
    /// </summary>
    private void UpdateAIButtonState(bool isConnected) {
        NaturalLanguageToggle.IsEnabled = isConnected;
        
        // Eğer internet yoksa ve AI modu aktifse, kapat
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
            LoadingStatus.Text = progress.Status;
            if (progress.IsIndeterminate) {
                LoadingProgress.IsIndeterminate = true;
                return;
            }

            LoadingProgress.IsIndeterminate = false;
            if (progress.Percentage >= 0 && progress.Percentage <= 100) {
                LoadingProgress.Value = progress.Percentage;
            }
        }));
    }

    private void HandleIndexError(string error) {
        Log($"⚠️ {error}");
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
    
    private void DisableNaturalLanguageMode() {
        _isNaturalLanguageMode = false;
        SearchWatermark.Text = "OmniSpot: Hafif Basit Masaüstü ve Tarayıcı";
        Log("📝 Standart arama modu aktif");
    }
    
    private void EnableGridView() {
        _isGridViewMode = true;
        ResultsList.Visibility = Visibility.Collapsed;
        ResultsGridScroll.Visibility = Visibility.Visible;
        Log("⊞ Grid görünümü aktif");
    }
    
    private void DisableGridView() {
        _isGridViewMode = false;
        ResultsList.Visibility = Visibility.Visible;
        ResultsGridScroll.Visibility = Visibility.Collapsed;
        Log("☰ Liste görünümü aktif");
    }
    
    private async Task InitializeAsync() {
        try {
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
        } catch (OperationCanceledException) when (_isPreparedForShutdown) {
        } catch (Exception ex) {
            Log($"❌ HATA: {ex.Message}");
            Log($"Stack trace: {ex.StackTrace}");
            await Dispatcher.InvokeAsync(() => {
                LoadingStatus.Text = $"Hata: {ex.Message}";
                LoadingProgress.IsIndeterminate = false;
                System.Windows.MessageBox.Show(
                    $"İndeksleme başarısız: {ex.Message}{Environment.NewLine}{Environment.NewLine}Detaylar için konsolu kontrol edin.",
                    "Hata",
                    MessageBoxButton.OK,
                    MessageBoxImage.Error);
            });
        }
    }

    private void CompleteIndexInitialization(
        IndexStartupResult startup,
        long elapsedMilliseconds) {
        var stats = startup.Stats;
        LoadingStatus.Text = $"{stats.FileCount} dosya, {stats.DirectoryCount} klasör indekslendi";
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

        LoadingOverlay.Visibility = Visibility.Collapsed;
        DesktopIconsScroll.Visibility = Visibility.Visible;
        SearchBox.Focus();
    }

    /// <summary>
    /// Dosya sistemi değişikliklerini işler ve UI'yi günceller.
    /// Debounce kullanarak çok sık güncelleme yapılmasını engeller.
    /// </summary>
    private void HandleFileSystemChange(FileChangeEvent evt)
    {
        if (_isPreparedForShutdown ||
            Dispatcher.HasShutdownStarted ||
            Dispatcher.HasShutdownFinished) return;

        // Log the change (sadece bir kere)
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
    
    /// <summary>
    /// Mevcut klasördeki ikonları akıllıca günceller (klasör içindeyken).
    /// </summary>
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
                var isNew = false;
                if (!existing.TryGetValue(entry.FullPath, out var viewModel))
                {
                    viewModel = new DesktopIconViewModel();
                    isNew = true;
                }

                viewModel.Name = entry.Name;
                viewModel.FullPath = entry.FullPath;
                viewModel.Icon = entry.IsDirectory
                    ? "📁"
                    : GetFileIcon(entry.Name);
                viewModel.IsDirectory = entry.IsDirectory;
                if (entry.IsDirectory)
                {
                    viewModel.SetFolderColors(entry.Name);
                }
                if (isNew)
                {
                    _ = LoadThumbnailAsync(viewModel);
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
    /// <summary>
    /// Ana sayfa (desktop) ikonlarını akıllıca günceller.
    /// </summary>
    private void RefreshDesktopIconsSmart()
    {
        var indexedRoots = _indexLifecycle.GetIndexedRoots();
        
        // Mevcut öğelerin path'lerini al
        var existingPaths = _desktopIcons.ToDictionary(d => d.FullPath, d => d, StringComparer.OrdinalIgnoreCase);
        var currentPaths = indexedRoots.Select(c => c.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        // Silinen öğeleri kaldır
        var toRemove = _desktopIcons.Where(d => !currentPaths.Contains(d.FullPath)).ToList();
        foreach (var item in toRemove)
        {
            _desktopIcons.Remove(item);
        }
        
        // Yeni öğeleri ekle
        foreach (var child in indexedRoots.OrderBy(n => n.Name))
        {
            if (!existingPaths.ContainsKey(child.FullPath))
            {
                var viewModel = new DesktopIconViewModel
                {
                    Name = child.Name,
                    FullPath = child.FullPath,
                    Icon = child.IsDirectory ? "📁" : GetFileIcon(child.Name),
                    IsDirectory = child.IsDirectory
                };
                
                // Klasör renklerini ayarla
                if (child.IsDirectory)
                {
                    viewModel.SetFolderColors(child.Name);
                }
                
                // Sıralı ekleme: Önce klasörler, sonra dosyalar (her grup kendi içinde alfabetik)
                var insertIndex = _desktopIcons.TakeWhile(d => 
                {
                    // Eğer her ikisi de klasör veya her ikisi de dosya ise alfabetik sırala
                    if (d.IsDirectory == child.IsDirectory)
                    {
                        return string.Compare(d.Name, child.Name, StringComparison.OrdinalIgnoreCase) < 0;
                    }
                    // Klasörler her zaman dosyalardan önce
                    return d.IsDirectory;
                }).Count();
                _desktopIcons.Insert(insertIndex, viewModel);
                
                // Async thumbnail yükleme
                _ = LoadThumbnailAsync(viewModel);
            }
        }
        
        Log($"🔄 Desktop güncellendi: {_desktopIcons.Count} öğe");
    }
    
    /// <summary>
    /// Arama sonuçlarını yeniler (dosya değişikliği sonrası).
    /// </summary>
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
                        Icon = isDirectory ? "📁" : GetFileIcon(result.Name),
                        IsDirectory = isDirectory
                    };
                    
                    // Klasör renklerini ayarla
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
            // A newer query owns the result surface.
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
        
        // Önce klasörler, sonra dosyalar - her grup alfabetik sıralı
        var sortedChildren = indexedRoots
            .OrderBy(n => !n.IsDirectory)  // false (klasör) önce, true (dosya) sonra
            .ThenBy(n => n.Name, StringComparer.OrdinalIgnoreCase);
        
        foreach (var child in sortedChildren) {
            var viewModel = new DesktopIconViewModel {
                Name = child.Name,
                FullPath = child.FullPath,
                Icon = child.IsDirectory ? "📁" : GetFileIcon(child.Name),
                IsDirectory = child.IsDirectory
            };
            
            // Klasör renklerini ayarla
            if (child.IsDirectory) {
                viewModel.SetFolderColors(child.Name);
            }
            
            _desktopIcons.Add(viewModel);
            
            // Async thumbnail yükleme - UI'yi bloklamaz
            _ = LoadThumbnailAsync(viewModel);
        }
        
        Log($"✅ Desktop ikonları yüklendi, thumbnail'ler arka planda yükleniyor...");
    }
    
    private async Task LoadThumbnailAsync(DesktopIconViewModel viewModel)
    {
        try
        {
            var thumbnail = await _thumbnailService.GetThumbnailAsync(
                viewModel.FullPath,
                THUMBNAIL_SIZE,
                CancellationToken.None
            );
            
            if (thumbnail != null)
            {
                // UI thread'inde güncelleme
                await Dispatcher.InvokeAsync(() => 
                {
                    viewModel.Thumbnail = thumbnail;
                });
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Thumbnail yükleme hatası ({viewModel.Name}): {ex.Message}");
        }
    }
    
    private async Task LoadSearchThumbnailAsync(SearchResultViewModel viewModel)
    {
        try
        {
            var thumbnail = await _thumbnailService.GetThumbnailAsync(
                viewModel.FullPath,
                THUMBNAIL_SIZE,
                CancellationToken.None
            );
            
            if (thumbnail != null)
            {
                await Dispatcher.InvokeAsync(() => 
                {
                    viewModel.Thumbnail = thumbnail;
                });
            }
        }
        catch
        {
            // Thumbnail yüklenemezse sessizce devam et
        }
    }
    
    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e) {
        if (!_isIndexed) {
            Log("Arama yapılamadı: İndeksleme henüz tamamlanmadı");
            return;
        }
        
        var query = SearchBox.Text;
        
        if (string.IsNullOrWhiteSpace(query)) {
            CancelCurrentSearch();
            
            // Show desktop icons, hide search results
            DesktopIconsScroll.Visibility = Visibility.Visible;
            ResultsContainer.Visibility = Visibility.Collapsed;
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
                await Task.Delay(DEBOUNCE_DELAY_MS, cancellation.Token);
            }

            cancellation.Token.ThrowIfCancellationRequested();
            if (!IsCurrentSearch(version)) return;

            DesktopIconsScroll.Visibility = Visibility.Collapsed;
            ResultsContainer.Visibility = Visibility.Visible;

            Log($"🔍 Arama sorgusu: '{query}'");
            var tokens = _searchDiagnostics.Tokenize(query);
            Log($"🔤 Query tokenler: [{string.Join(", ", tokens)}]");
            ShowSearchingIndicator(query);

            await RunSearchAsync(query, version, cancellation.Token);
        } catch (OperationCanceledException) {
            // A newer request owns the UI. The stale request must not mutate it.
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
    
    /// <summary>
    /// Aranıyor göstergesini gösterir
    /// </summary>
    private void ShowSearchingIndicator(string query) {
        SearchingPanel.Visibility = Visibility.Visible;
        NoResultsPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Collapsed;
        ResultsGridScroll.Visibility = Visibility.Collapsed;
        
        // Yeni arama başladığında önceki fallback uyarısını gizle
        // (Eğer bu arama başarılı olursa uyarı kapanır, olmazsa tekrar gösterilir)
        FallbackWarningBanner.Visibility = Visibility.Collapsed;
        
        // AI modunda farklı mesaj
        if (_isNaturalLanguageMode) {
            SearchingText.Text = "🤖 AI ile aranıyor...";
        } else {
            SearchingText.Text = "🔍 Aranıyor...";
        }
    }
    
    /// <summary>
    /// Tüm sonuç panellerini gizler
    /// </summary>
    private void HideAllPanels() {
        SearchingPanel.Visibility = Visibility.Collapsed;
        NoResultsPanel.Visibility = Visibility.Collapsed;
        ErrorPanel.Visibility = Visibility.Collapsed;
        FallbackWarningBanner.Visibility = Visibility.Collapsed;
    }
    
    /// <summary>
    /// Hata panelini gösterir
    /// </summary>
    private void ShowError(string title, string message) {
        SearchingPanel.Visibility = Visibility.Collapsed;
        NoResultsPanel.Visibility = Visibility.Collapsed;
        ResultsList.Visibility = Visibility.Collapsed;
        ResultsGridScroll.Visibility = Visibility.Collapsed;
        
        ErrorPanel.Visibility = Visibility.Visible;
        ErrorTitle.Text = title;
        ErrorMessage.Text = message;
    }
    
    /// <summary>
    /// AI fallback uyarı banner'ını gösterir
    /// </summary>
    private void ShowFallbackWarning(string reason) {
        FallbackWarningBanner.Visibility = Visibility.Visible;
        FallbackReasonText.Text = reason;
        Log($"⚠️ AI fallback: {reason}");
    }
    
    /// <summary>
    /// Fallback banner'ını kapatır
    /// </summary>
    private void CloseFallbackBanner_Click(object sender, RoutedEventArgs e) {
        FallbackWarningBanner.Visibility = Visibility.Collapsed;
    }
    
    /// <summary>
    /// Delta sync warning banner'ını kapatır
    /// </summary>
    private void CloseDeltaSyncBanner_Click(object sender, RoutedEventArgs e) {
        DeltaSyncWarningBanner.Visibility = Visibility.Collapsed;
    }
    
    /// <summary>
    /// Delta sync warning banner'ını gösterir
    /// </summary>
    private void ShowDeltaSyncWarning(string details) {
        DeltaSyncWarningBanner.Visibility = Visibility.Visible;
        DeltaSyncWarningText.Text = details;
    }
    
    /// <summary>
    /// Delta sync progress'i günceller
    /// </summary>
    private void UpdateDeltaSyncProgress(int processed, int total, int percentage) {
        DeltaSyncProgressBar.IsIndeterminate = false;
        DeltaSyncProgressBar.Value = percentage;
        DeltaSyncDetails.Text = $" - %{percentage}";
        DeltaSyncMinimizedText.Text = $"%{percentage}";
        
        // Delta sync tamamlandı mı?
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
    
    /// <summary>
    /// Delta sync panelini minimize eder
    /// </summary>
    private void MinimizeDeltaSync_Click(object sender, RoutedEventArgs e) {
        DeltaSyncPanel.Visibility = Visibility.Collapsed;
        DeltaSyncMinimized.Visibility = Visibility.Visible;
    }
    
    /// <summary>
    /// Delta sync panelini genişletir
    /// </summary>
    private void ExpandDeltaSync_Click(object sender, MouseButtonEventArgs e) {
        DeltaSyncPanel.Visibility = Visibility.Visible;
        DeltaSyncMinimized.Visibility = Visibility.Collapsed;
    }
    
    /// <summary>
    /// Folder loading indicator'ı gösterir
    /// </summary>
    private void ShowFolderLoadingIndicator(string folderPath) {
        var folderName = Path.GetFileName(folderPath);
        if (string.IsNullOrEmpty(folderName)) folderName = folderPath;
        
        FolderLoadingTitle.Text = $"📂 {folderName}";
        FolderLoadingPanel.Visibility = Visibility.Visible;
    }
    
    /// <summary>
    /// Folder loading indicator'ı gizler
    /// </summary>
    private void HideFolderLoadingIndicator() {
        FolderLoadingPanel.Visibility = Visibility.Collapsed;
    }
    
    /// <summary>
    /// Tekrar Dene butonuna tıklandığında
    /// </summary>
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
            
            // Aramayı yeniden başlat
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
            var outcome = await _searchService.SearchAsync(
                new SearchRequest(
                    query,
                    _isNaturalLanguageMode,
                    _connectivityMonitor.IsConnected),
                cancellationToken);

            cancellationToken.ThrowIfCancellationRequested();
            if (!IsCurrentSearch(searchVersion)) return;

            LogSearchOutcome(outcome);

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

            RenderSearchResults(outcome.Results, cancellationToken);
        } catch (OperationCanceledException) {
            throw;
        } catch (Exception ex) {
            if (!IsCurrentSearch(searchVersion)) return;

            Log($"❌ Arama hatası: {ex.Message}");
            Log($"Stack: {ex.StackTrace}");
            ShowError("Arama sırasında bir hata oluştu", ex.Message);
        }
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
        _searchResults.Clear();

        if (results.Count == 0) {
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

        foreach (var result in results) {
            cancellationToken.ThrowIfCancellationRequested();
            var isDirectory = result.IsDirectory;
            var viewModel = new SearchResultViewModel {
                Name = result.Name,
                FullPath = result.FullPath,
                Score = result.Score,
                Icon = isDirectory ? "📁" : GetFileIcon(result.Name),
                IsDirectory = isDirectory
            };

            if (isDirectory) {
                viewModel.SetFolderColors(result.Name);
            }

            _searchResults.Add(viewModel);
            _ = LoadSearchResultThumbnailAsync(viewModel);
        }
    }

    private async Task LoadSearchResultThumbnailAsync(SearchResultViewModel viewModel)
    {
        try
        {
            var thumbnail = await _thumbnailService.GetThumbnailAsync(
                viewModel.FullPath,
                THUMBNAIL_SIZE,
                CancellationToken.None
            );
            
            if (thumbnail != null)
            {
                await Dispatcher.InvokeAsync(() => 
                {
                    viewModel.Thumbnail = thumbnail;
                });
            }
        }
        catch (Exception ex)
        {
            Log($"⚠️ Search result thumbnail error ({viewModel.Name}): {ex.Message}");
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
        // ESC - Kapat
        if (e.Key == Key.Escape) {
            SafeClose();
            return;
        }
        
        // File operation keyboard shortcuts
        bool ctrlPressed = (Keyboard.Modifiers & ModifierKeys.Control) == ModifierKeys.Control;
        bool shiftPressed = (Keyboard.Modifiers & ModifierKeys.Shift) == ModifierKeys.Shift;
        bool altPressed = (Keyboard.Modifiers & ModifierKeys.Alt) == ModifierKeys.Alt;
        
        // Hover edilen öğe varsa onu kullan, yoksa seçili öğeyi kullan
        var targetPath = _hoveredItemPath ?? _selectedItemPath;
        var targetItem = _hoveredItem;
        
        // Ctrl+C - Kopyala (hover edilen öğe)
        if (ctrlPressed && e.Key == Key.C && !string.IsNullOrEmpty(targetPath)) {
            CopyItemToClipboard(targetPath, isCut: false);
            e.Handled = true;
            return;
        }
        
        // Ctrl+X - Kes (hover edilen öğe)
        if (ctrlPressed && e.Key == Key.X && !string.IsNullOrEmpty(targetPath)) {
            CopyItemToClipboard(targetPath, isCut: true);
            // Kesilen öğeyi silik göster
            if (targetItem != null) {
                // Önceki kesilen öğeyi normal yap
                if (_cutItem != null) {
                    _cutItem.IsCut = false;
                }
                targetItem.IsCut = true;
                _cutItem = targetItem;
            }
            e.Handled = true;
            return;
        }
        
        // Ctrl+V - Yapıştır
        if (ctrlPressed && e.Key == Key.V) {
            PasteFromClipboard();
            e.Handled = true;
            return;
        }
        
        // Ctrl+Shift+N - Yeni Klasör
        if (ctrlPressed && shiftPressed && e.Key == Key.N) {
            ContextMenu_NewFolder(this, new RoutedEventArgs());
            e.Handled = true;
            return;
        }
        
        // F2 - Yeniden Adlandır (hover edilen öğe)
        if (e.Key == Key.F2 && !string.IsNullOrEmpty(targetPath)) {
            RenameItem(targetPath);
            e.Handled = true;
            return;
        }
        
        // Delete - Sil (hover edilen öğe)
        if (e.Key == Key.Delete && !string.IsNullOrEmpty(targetPath)) {
            DeleteItem(targetPath);
            e.Handled = true;
            return;
        }
        
        // F5 - Yenile
        if (e.Key == Key.F5) {
            RefreshCurrentFolder();
            ShowFeedback("🔄 Yenilendi");
            e.Handled = true;
            return;
        }
        
        // Alt+Enter - Özellikler (hover edilen öğe)
        if (altPressed && e.Key == Key.Enter && !string.IsNullOrEmpty(targetPath)) {
            _fileOperations.ShowProperties(targetPath);
            e.Handled = true;
            return;
        }
    }
    
    /// <summary>
    /// Kullanıcıya geçici geri bildirim gösterir
    /// </summary>
    private void ShowFeedback(string message) {
        Log($"💬 {message}");
        // TODO: Toast notification eklenebilir
    }
    
    /// <summary>
    /// Öğeyi panoya kopyalar
    /// </summary>
    private void CopyItemToClipboard(string path, bool isCut) {
        _clipboardPath = path;
        _isCutOperation = isCut;
        
        // Önceki kesilen öğeyi normal yap (kopyalama durumunda)
        if (!isCut && _cutItem != null) {
            _cutItem.IsCut = false;
            _cutItem = null;
        }
        
        var name = Path.GetFileName(path);
        var operation = isCut ? "✂️ Kesildi" : "📋 Kopyalandı";
        ShowFeedback($"{operation}: {name}");
    }
    
    /// <summary>
    /// Panodan yapıştırır
    /// </summary>
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

    /// <summary>
    /// Öğeyi yeniden adlandırır
    /// </summary>
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
    
    /// <summary>
    /// Öğeyi siler (Geri Dönüşüm Kutusuna)
    /// </summary>
    private void DeleteItem(string path) {
        var name = Path.GetFileName(path);
        var result = System.Windows.MessageBox.Show(
            $"'{name}' öğesini silmek istediğinize emin misiniz?\n\nBu öğe Geri Dönüşüm Kutusu'na taşınacak.",
            "Silme Onayı",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);
        
        if (result == MessageBoxResult.Yes) {
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
            System.Windows.MessageBox.Show($"Açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    
    /// <summary>
    /// Klasörü uygulama içinde açar ve içeriğini gösterir
    /// </summary>
    private async Task OpenFolderInApp(string folderPath) {
        try {
            Log($"📂 Klasör açılıyor: {folderPath}");
            
            // Show loading indicator
            ShowFolderLoadingIndicator(folderPath);
            
            try {
                // Klasör içeriğini ASYNC yükle (büyük klasörler için optimize edildi)
                if (!await LoadFolderContentsAsync(
                        folderPath,
                        ensureSynchronized: true)) {
                    return;
                }

                _currentFolderPath = folderPath;
                
                // UI'ı güncelle
                SearchBox.Clear();
                ResultsContainer.Visibility = Visibility.Collapsed;
                DesktopIconsScroll.Visibility = Visibility.Visible;
                
                // Geri butonunu göster
                BackButton.Visibility = Visibility.Visible;
                
                // Watermark'ı güncelle
                var folderName = System.IO.Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(folderName)) folderName = folderPath;
                SearchWatermark.Text = $"📂 {folderName}";
            } finally {
                // Hide loading overlay
                HideFolderLoadingIndicator();
            }
            
        } catch (Exception ex) {
            Log($"❌ Klasör açılamadı: {ex.Message}");
            System.Windows.MessageBox.Show($"Klasör açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    
    /// <summary>
    /// Maximum number of items to load in a folder (performance limit)
    /// </summary>
    private const int MAX_FOLDER_ITEMS = 1000;
    
    /// <summary>
    /// Thumbnail loading batch size
    /// </summary>
    private const int THUMBNAIL_BATCH_SIZE = 20;
    
    /// <summary>
    /// Klasör içeriğini ASYNC yükler - büyük klasörler için optimize edildi
    /// </summary>
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
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_folderLoadCancellation, cancellation)) {
                return false;
            }

            _desktopIcons.Clear();
            EmptyFolderPanel.Visibility = Visibility.Collapsed;

            var items = page.Entries.Select(entry => {
                var viewModel = new DesktopIconViewModel {
                    Name = entry.Name,
                    FullPath = entry.FullPath,
                    Icon = entry.IsDirectory ? "📁" : GetFileIcon(entry.Name),
                    IsDirectory = entry.IsDirectory
                };

                if (entry.IsDirectory) {
                    viewModel.SetFolderColors(entry.Name);
                }

                return viewModel;
            }).ToList();

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
                _ = LoadThumbnailsInBatchesAsync(items);
            }

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

    /// <summary>
    /// Thumbnail'leri batch'ler halinde yükler (UI'ı bloklamaz)
    /// </summary>
    private async Task LoadThumbnailsInBatchesAsync(List<DesktopIconViewModel> items) {
        // Process in batches to avoid overwhelming the system
        for (int i = 0; i < items.Count; i += THUMBNAIL_BATCH_SIZE) {
            var batch = items.Skip(i).Take(THUMBNAIL_BATCH_SIZE).ToList();
            
            // Load batch in parallel
            var tasks = batch.Select(async icon => {
                try {
                    var thumbnail = await _thumbnailService.GetThumbnailAsync(
                        icon.FullPath, 
                        THUMBNAIL_SIZE,
                        CancellationToken.None);
                    
                    if (thumbnail != null) {
                        await Dispatcher.InvokeAsync(() => {
                            icon.Thumbnail = thumbnail;
                        }, System.Windows.Threading.DispatcherPriority.Background);
                    }
                } catch { }
            });
            
            await Task.WhenAll(tasks);
            
            // Small delay between batches to keep UI responsive
            await Task.Delay(10);
        }
    }
    
    /// <summary>
    /// Klasör içeriğini yükler (legacy - senkron versiyon, küçük klasörler için)
    /// </summary>
    private void LoadFolderContents(string folderPath) {
        // Delegate to async version for large folder support
        _ = LoadFolderContentsAsync(folderPath);
    }
    
    /// <summary>
    /// Klasör için thumbnail yükler
    /// </summary>
    private async Task LoadFolderThumbnailAsync(DesktopIconViewModel icon) {
        try {
            var thumbnail = await _thumbnailService.GetThumbnailAsync(icon.FullPath, THUMBNAIL_SIZE);
            if (thumbnail != null) {
                await Dispatcher.InvokeAsync(() => {
                    icon.Thumbnail = thumbnail;
                });
            }
        } catch { }
    }
    
    /// <summary>
    /// Dosya adına veya uzantısına göre ikon döndürür
    /// </summary>
    private string GetFileIcon(string filenameOrExtension) {
        // Uzantıyı al (eğer dosya adı verilmişse)
        var ext = filenameOrExtension.StartsWith(".") 
            ? filenameOrExtension.ToLowerInvariant() 
            : System.IO.Path.GetExtension(filenameOrExtension).ToLowerInvariant();
            
        return ext switch {
            ".pdf" => "📕",
            ".doc" or ".docx" => "📘",
            ".xls" or ".xlsx" => "📗",
            ".ppt" or ".pptx" => "📙",
            ".txt" or ".md" => "📄",
            ".jpg" or ".jpeg" or ".png" or ".gif" or ".bmp" or ".webp" => "🖼️",
            ".mp3" or ".wav" or ".flac" or ".m4a" or ".aac" => "🎵",
            ".mp4" or ".avi" or ".mkv" or ".mov" or ".wmv" => "🎬",
            ".zip" or ".rar" or ".7z" or ".tar" or ".gz" => "📦",
            ".exe" or ".msi" => "⚙️",
            ".lnk" => "🔗",
            ".html" or ".htm" => "🌐",
            ".cs" or ".js" or ".py" or ".java" or ".cpp" => "💻",
            _ => "📄"
        };
    }
    
    /// <summary>
    /// Üst klasöre çık
    /// </summary>
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

        _ = OpenFolderInApp(parent);
    }
    /// <summary>
    /// Ana ekrana (Desktop) dön
    /// </summary>
    private void GoToHome() {
        _currentFolderPath = null;
        BackButton.Visibility = Visibility.Collapsed;
        LoadDesktopIcons();
        SearchWatermark.Text = "OmniSpot: Hafif Basit Masaüstü ve Tarayıcı";
    }
    
    /// <summary>
    /// Geri butonuna tıklandığında
    /// </summary>
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
        // Eğer MinimizeToTray aktifse, ForceExit kullan
        if (_appSettings.MinimizeToTrayOnClose) {
            MinimizeToTray();
        } else {
            ForceExit();
        }
    }
    
    #region File Operations & Context Menu
    
    /// <summary>
    /// Dosya/klasör üzerine mouse geldiğinde (hover)
    /// </summary>
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
    
    /// <summary>
    /// Mouse öğeden ayrıldığında
    /// </summary>
    private void FileItem_MouseLeave(object sender, System.Windows.Input.MouseEventArgs e) {
        _hoveredItemPath = null;
        _hoveredItem = null;
    }
    
    /// <summary>
    /// Dosya/klasör üzerinde sağ tık yapıldığında
    /// </summary>
    private void FileItem_RightClick(object sender, MouseButtonEventArgs e) {
        if (sender is FrameworkElement element) {
            // Button'un Tag'ından veya DataContext'ten path al
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
    
    /// <summary>
    /// Boş alana sağ tık yapıldığında
    /// </summary>
    private void EmptyArea_RightClick(object sender, MouseButtonEventArgs e) {
        // Eğer bir dosya/klasör üzerinde değilse boş alan menüsü göster
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
            System.Windows.MessageBox.Show(
                $"Birlikte aç hatası: {ex.Message}",
                "Hata",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
        
        // ViewModel'i bul
        var item = _desktopIcons.FirstOrDefault(i => 
            string.Equals(i.FullPath, path, StringComparison.OrdinalIgnoreCase));
        
        CopyItemToClipboard(path, isCut: true);
        
        // Kesilen öğeyi silik göster
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

    /// <summary>
    /// Context menu'den path bilgisini alır
    /// </summary>
    private string? GetPathFromContextMenu(object sender) {
        if (sender is MenuItem menuItem && menuItem.Parent is ContextMenu contextMenu) {
            // PlacementTarget'tan path'i al
            if (contextMenu.PlacementTarget is FrameworkElement element) {
                // Button'un Tag'ından path al (Grid view için)
                if (element.Tag is string tagPath) {
                    return tagPath;
                }
                // DataContext'ten path al (List view için)
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
        
        // Fallback: _selectedItemPath kullan
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
            System.Windows.MessageBox.Show(
                $"Klasör oluşturma hatası: {ex.Message}",
                "Hata",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
            System.Windows.MessageBox.Show(
                $"Dosya oluşturma hatası: {ex.Message}",
                "Hata",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
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
    
    /// <summary>
    /// Belirli bir öğenin parent klasörünü yeniler (öğenin bulunduğu klasöre göre)
    /// </summary>
    private void RefreshFolderContaining(string itemPath) {
        var parentFolder = Path.GetDirectoryName(itemPath);
        if (string.IsNullOrEmpty(parentFolder)) {
            RefreshCurrentFolder();
            return;
        }
        
        // Eğer parent klasör şu anda görüntülenen klasörse
        if (_currentFolderPath != null && 
            string.Equals(parentFolder.TrimEnd('\\', '/'), _currentFolderPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase)) {
            LoadFolderContents(_currentFolderPath);
        }
        // Eğer ana sayfadaysak (desktop icons), ana sayfayı yenile
        else if (_currentFolderPath == null) {
            LoadDesktopIcons();
        }
        // Farklı bir klasördeyiz, RefreshCurrentFolder'ı kullan
        else {
            RefreshCurrentFolder();
        }
    }
    
    /// <summary>
    /// Görüntüdeki öğeyi yerinde günceller (ana sayfaya dönmeden)
    /// </summary>
    private void UpdateItemInView(string oldPath, string newPath, string newName) {
        // Desktop ikonlarında ara
        var icon = _desktopIcons.FirstOrDefault(i => 
            string.Equals(i.FullPath, oldPath, StringComparison.OrdinalIgnoreCase));
        
        if (icon != null) {
            icon.Name = newName;
            icon.FullPath = newPath;
            Log($"✅ Görünüm güncellendi: {newName}");
        } else {
            // Bulunamadıysa klasörü yenile
            RefreshCurrentFolder();
        }
    }
    
    /// <summary>
    /// Görüntüden öğeyi kaldırır (ana sayfaya dönmeden)
    /// </summary>
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
