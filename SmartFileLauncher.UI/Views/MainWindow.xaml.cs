using System.Diagnostics;
using System.Windows.Threading;
using System.Linq;
using System.Windows;
using System.Windows.Input;
using System.Windows.Controls;
using System.Collections.ObjectModel;
using System.Threading.Tasks;
using System.Text.Json;
using System.Windows.Media;
using System.Net.NetworkInformation;
using System.IO;
using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.Models;

namespace SmartFileLauncher.UI.Views;

public class DesktopIconViewModel : System.ComponentModel.INotifyPropertyChanged {
    private string _name = "";
    private string _fullPath = "";
    private string _icon = "📄";
    private bool _isDirectory = false;
    private bool _isCut = false;
    private double _opacity = 1.0;
    
    // Thread-safe frozen brushes (cached and reused)
    private static readonly Dictionary<string, System.Windows.Media.Brush> _brushCache = new();
    private static readonly object _brushCacheLock = new();
    
    private static System.Windows.Media.Brush GetOrCreateFrozenBrush(byte r, byte g, byte b)
    {
        var key = $"{r},{g},{b}";
        lock (_brushCacheLock)
        {
            if (_brushCache.TryGetValue(key, out var cached))
                return cached;
            
            var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
            brush.Freeze(); // Makes it thread-safe
            _brushCache[key] = brush;
            return brush;
        }
    }
    
    public string Name { 
        get => _name; 
        set { 
            if (_name != value) { 
                _name = value; 
                OnPropertyChanged(nameof(Name)); 
            } 
        } 
    }
    
    public string FullPath { 
        get => _fullPath; 
        set { 
            if (_fullPath != value) { 
                _fullPath = value; 
                OnPropertyChanged(nameof(FullPath)); 
            } 
        } 
    }
    
    public string Icon { 
        get => _icon; 
        set { 
            if (_icon != value) { 
                _icon = value; 
                OnPropertyChanged(nameof(Icon)); 
            } 
        } 
    }
    
    public bool IsDirectory { 
        get => _isDirectory; 
        set { 
            if (_isDirectory != value) { 
                _isDirectory = value; 
                OnPropertyChanged(nameof(IsDirectory)); 
            } 
        } 
    }
    
    /// <summary>
    /// Kes işlemi yapıldığında true olur - silik görünüm için
    /// </summary>
    public bool IsCut { 
        get => _isCut; 
        set { 
            if (_isCut != value) { 
                _isCut = value; 
                Opacity = value ? 0.5 : 1.0; // Kesilen öğe %50 şeffaf
                OnPropertyChanged(nameof(IsCut)); 
            } 
        } 
    }
    
    /// <summary>
    /// Öğenin opaklığı (0-1 arası, kes işleminde 0.5)
    /// </summary>
    public double Opacity { 
        get => _opacity; 
        set { 
            if (_opacity != value) { 
                _opacity = value; 
                OnPropertyChanged(nameof(Opacity)); 
            } 
        } 
    }
    
    // Folder colors based on folder type (using frozen brushes for thread safety)
    private System.Windows.Media.Brush _folderColor = GetOrCreateFrozenBrush(99, 102, 241);
    private System.Windows.Media.Brush _folderColorLight = GetOrCreateFrozenBrush(129, 140, 248);
    
    public System.Windows.Media.Brush FolderColor { 
        get => _folderColor; 
        set { 
            if (_folderColor != value) { 
                _folderColor = value; 
                OnPropertyChanged(nameof(FolderColor)); 
            } 
        } 
    }
    
    public System.Windows.Media.Brush FolderColorLight { 
        get => _folderColorLight; 
        set { 
            if (_folderColorLight != value) { 
                _folderColorLight = value; 
                OnPropertyChanged(nameof(FolderColorLight)); 
            } 
        } 
    }
    
    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
    }
    
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    
    private void OnPropertyChanged(string propertyName) {
        PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(propertyName));
    }
    
    /// <summary>
    /// Klasör adına göre renk belirler (thread-safe frozen brushes kullanır)
    /// </summary>
    public void SetFolderColors(string folderName)
    {
        var name = folderName.ToLowerInvariant();
        
        if (name.Contains("document") || name.Contains("belgeler") || name == "documents")
        {
            // Blue
            FolderColor = GetOrCreateFrozenBrush(59, 130, 246);
            FolderColorLight = GetOrCreateFrozenBrush(96, 165, 250);
        }
        else if (name.Contains("download") || name.Contains("indirilenler") || name == "downloads")
        {
            // Green
            FolderColor = GetOrCreateFrozenBrush(16, 185, 129);
            FolderColorLight = GetOrCreateFrozenBrush(52, 211, 153);
        }
        else if (name.Contains("desktop") || name.Contains("masaüstü") || name == "masaüstü")
        {
            // Purple
            FolderColor = GetOrCreateFrozenBrush(139, 92, 246);
            FolderColorLight = GetOrCreateFrozenBrush(167, 139, 250);
        }
        else if (name.Contains("music") || name.Contains("müzik") || name == "music")
        {
            // Pink
            FolderColor = GetOrCreateFrozenBrush(236, 72, 153);
            FolderColorLight = GetOrCreateFrozenBrush(244, 114, 182);
        }
        else if (name.Contains("picture") || name.Contains("resim") || name == "pictures")
        {
            // Orange/Amber
            FolderColor = GetOrCreateFrozenBrush(245, 158, 11);
            FolderColorLight = GetOrCreateFrozenBrush(251, 191, 36);
        }
        else if (name.Contains("video") || name.Contains("videolar") || name == "videos")
        {
            // Red
            FolderColor = GetOrCreateFrozenBrush(239, 68, 68);
            FolderColorLight = GetOrCreateFrozenBrush(248, 113, 113);
        }
        else
        {
            // Default Indigo
            FolderColor = GetOrCreateFrozenBrush(99, 102, 241);
            FolderColorLight = GetOrCreateFrozenBrush(129, 140, 248);
        }
    }
}

public class SearchResultViewModel : System.ComponentModel.INotifyPropertyChanged {
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public double Score { get; set; }
    public string Icon { get; set; } = "📄";
    public bool IsDirectory { get; set; } = false;
    
    // Thread-safe frozen brush helper (reuses DesktopIconViewModel's cache)
    private static System.Windows.Media.Brush GetFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        brush.Freeze(); // Makes it thread-safe
        return brush;
    }
    
    // Folder colors based on folder type (using frozen brushes)
    private static readonly System.Windows.Media.Brush DefaultFolderColor = GetFrozenBrush(99, 102, 241);
    private static readonly System.Windows.Media.Brush DefaultFolderColorLight = GetFrozenBrush(129, 140, 248);
    
    public System.Windows.Media.Brush FolderColor { get; set; } = DefaultFolderColor;
    public System.Windows.Media.Brush FolderColorLight { get; set; } = DefaultFolderColorLight;
    
    private ImageSource? _thumbnail;
    public ImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new System.ComponentModel.PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }
    }
    
    public event System.ComponentModel.PropertyChangedEventHandler? PropertyChanged;
    
    /// <summary>
    /// Klasör adına göre renk belirler (thread-safe frozen brushes kullanır)
    /// </summary>
    public void SetFolderColors(string folderName)
    {
        var name = folderName.ToLowerInvariant();
        
        if (name.Contains("document") || name.Contains("belgeler") || name == "documents")
        {
            // Blue
            FolderColor = GetFrozenBrush(59, 130, 246);
            FolderColorLight = GetFrozenBrush(96, 165, 250);
        }
        else if (name.Contains("download") || name.Contains("indirilenler") || name == "downloads")
        {
            // Green
            FolderColor = GetFrozenBrush(16, 185, 129);
            FolderColorLight = GetFrozenBrush(52, 211, 153);
        }
        else if (name.Contains("desktop") || name.Contains("masaüstü") || name == "masaüstü")
        {
            // Purple
            FolderColor = GetFrozenBrush(139, 92, 246);
            FolderColorLight = GetFrozenBrush(167, 139, 250);
        }
        else if (name.Contains("music") || name.Contains("müzik") || name == "music")
        {
            // Pink
            FolderColor = GetFrozenBrush(236, 72, 153);
            FolderColorLight = GetFrozenBrush(244, 114, 182);
        }
        else if (name.Contains("picture") || name.Contains("resim") || name == "pictures")
        {
            // Orange/Amber
            FolderColor = GetFrozenBrush(245, 158, 11);
            FolderColorLight = GetFrozenBrush(251, 191, 36);
        }
        else if (name.Contains("video") || name.Contains("videolar") || name == "videos")
        {
            // Red
            FolderColor = GetFrozenBrush(239, 68, 68);
            FolderColorLight = GetFrozenBrush(248, 113, 113);
        }
        else
        {
            // Default Indigo
            FolderColor = DefaultFolderColor;
            FolderColorLight = DefaultFolderColorLight;
        }
    }
}

public partial class MainWindow : Window {
    private readonly IIndexLifecycleService _indexLifecycle;
    private readonly ISearchApplicationService _searchService;
    private readonly IThumbnailService _thumbnailService;
    private readonly IFolderBrowserService _folderBrowser;
    private readonly IFileOperationService _fileOperations;
    private readonly GlobalHotkeyService _hotkeyService;
    private readonly ApplicationLog _applicationLog;
    private string _desktopPath = ""; // Desktop path for icon loading
    private string? _currentFolderPath = null; // Currently browsed folder (null = home/desktop)
    private List<string> _indexedRootPaths = new(); // İndekslenen kök dizinler
    private readonly ObservableCollection<DesktopIconViewModel> _desktopIcons = new();
    private readonly ObservableCollection<SearchResultViewModel> _searchResults = new();
    private bool _isIndexed = false;
    private bool _isNaturalLanguageMode = false;
    private bool _isGridViewMode = false; // Grid görünümü için
    private bool _hasInternetConnection = true; // İnternet bağlantısı durumu
    private System.Threading.Timer? _internetCheckTimer;
    private System.Threading.Timer? _fileChangeDebounceTimer; // Dosya değişikliği debounce
    private const int FILE_CHANGE_DEBOUNCE_MS = 1000; // 1 saniye debounce (daha az kasma için artırıldı)
    private bool _isProcessingFileChange = false; // Çift işleme engeli
    private CancellationTokenSource? _currentSearchCancellation;
    private CancellationTokenSource? _folderLoadCancellation;
    private readonly CancellationTokenSource _lifetimeCancellation = new();
    private long _searchVersion;
    private bool _isPreparedForShutdown;
    private string _lastSearchQuery = ""; // Son arama sorgusu (retry için)
    private const int DEBOUNCE_DELAY_MS = 1200; // 1.2 seconds delay after last keystroke (increased from 400ms)
    private const int THUMBNAIL_SIZE = 128; // Thumbnail boyutu
    private const int INTERNET_CHECK_INTERVAL_MS = 10000; // 10 saniyede bir kontrol
    
    // Global Hotkey ve Ayarlar
    private AppSettings _appSettings;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    
    public MainWindow(
        AppSettings appSettings,
        IIndexLifecycleService indexLifecycle,
        ISearchApplicationService searchService,
        IThumbnailService thumbnailService,
        IFolderBrowserService folderBrowser,
        IFileOperationService fileOperations,
        GlobalHotkeyService hotkeyService,
        ApplicationLog applicationLog) {
        _appSettings = appSettings ?? throw new ArgumentNullException(nameof(appSettings));
        _indexLifecycle = indexLifecycle ?? throw new ArgumentNullException(nameof(indexLifecycle));
        _searchService = searchService ?? throw new ArgumentNullException(nameof(searchService));
        _thumbnailService = thumbnailService ?? throw new ArgumentNullException(nameof(thumbnailService));
        _folderBrowser = folderBrowser ?? throw new ArgumentNullException(nameof(folderBrowser));
        _fileOperations = fileOperations ?? throw new ArgumentNullException(nameof(fileOperations));
        _hotkeyService = hotkeyService ?? throw new ArgumentNullException(nameof(hotkeyService));
        _applicationLog = applicationLog ?? throw new ArgumentNullException(nameof(applicationLog));

        InitializeComponent();

        _applicationLog.MessageWritten += HandleApplicationLogMessage;
        foreach (var message in _applicationLog.GetSnapshot()) {
            AppendLogMessage(message);
        }

        _indexLifecycle.ProgressChanged += HandleIndexProgress;
        _indexLifecycle.Error += HandleIndexError;
        _indexLifecycle.FileChanged += HandleFileSystemChange;
        _indexLifecycle.ReconciliationProgressChanged += HandleReconciliationProgress;
        
        // Wire up events
        SearchBox.TextChanged += SearchBox_TextChanged;
        SearchBox.GotFocus += (_, __) => SearchWatermark.Visibility = Visibility.Collapsed;
        SearchBox.LostFocus += (_, __) => {
            if (string.IsNullOrWhiteSpace(SearchBox.Text)) 
                SearchWatermark.Visibility = Visibility.Visible;
        };
        SearchBox.KeyDown += SearchBox_KeyDown;
        ResultsList.MouseDoubleClick += (_, __) => OpenSelected();
        ConsoleToggleButton.Click += (_, __) => ToggleConsole();
        ClearConsoleButton.Click += (_, __) => ClearConsole();
        NaturalLanguageToggle.Checked += (_, __) => EnableNaturalLanguageMode();
        NaturalLanguageToggle.Unchecked += (_, __) => DisableNaturalLanguageMode();
        ViewModeToggle.Checked += (_, __) => EnableGridView();
        ViewModeToggle.Unchecked += (_, __) => DisableGridView();
        
        DesktopIcons.ItemsSource = _desktopIcons;
        ResultsList.ItemsSource = _searchResults;
        
        Log("=== OmniSpot Başlatıldı ===");
        Log("OmniSpot: Hafif Basit Masaüstü ve Tarayıcı");
        
        // İnternet bağlantısı kontrolü başlat
        CheckInternetConnection();
        StartInternetMonitoring();
        
        // Global hotkey ve system tray ayarla
        SetupGlobalHotkey();
        SetupSystemTray();
        
        // Pencere kapatma olayını yakala
        Closing += MainWindow_Closing;
        
        // Ayarlardan varsayılan modları uygula
        ApplyDefaultSettings();
        
        // Start async indexing after window loads
        Loaded += async (_, __) => await InitializeAsync();
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
    
    /// <summary>
    /// Global hotkey servisini başlatır
    /// </summary>
    private void SetupGlobalHotkey() {
        _hotkeyService.HotkeyPressed += OnHotkeyPressed;
        
        // Pencere yüklendikten sonra hotkey'i kaydet
        SourceInitialized += (s, e) => {
            _hotkeyService.Initialize(this);
            var modifiers = (GlobalHotkeyService.ModifierKeys)_appSettings.HotkeyModifiers;
            var key = _appSettings.HotkeyKey;
            
            if (_hotkeyService.RegisterHotkey(modifiers, key)) {
                Log($"✅ Global kısayol kaydedildi: {_hotkeyService.GetHotkeyString()}");
            } else {
                Log($"⚠️ Global kısayol kaydedilemedi: {_hotkeyService.GetHotkeyString()}");
            }
        };
    }
    
    /// <summary>
    /// System tray ikonunu ayarlar
    /// </summary>
    private void SetupSystemTray() {
        _notifyIcon = new System.Windows.Forms.NotifyIcon();
        _notifyIcon.Text = "OmniSpot";
        
        // Varsayılan ikon (uygulama ikonu)
        try {
            var iconPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "omnispot.ico");
            if (System.IO.File.Exists(iconPath)) {
                _notifyIcon.Icon = new System.Drawing.Icon(iconPath);
            } else {
                // Varsayılan sistem ikonu
                _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
            }
        } catch {
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }
        
        // Çift tıklama ile pencereyi aç
        _notifyIcon.DoubleClick += (s, e) => ShowAndActivate();
        
        // Sağ tık menüsü
        var contextMenu = new System.Windows.Forms.ContextMenuStrip();
        
        var showItem = new System.Windows.Forms.ToolStripMenuItem("Göster");
        showItem.Click += (s, e) => ShowAndActivate();
        contextMenu.Items.Add(showItem);
        
        var settingsItem = new System.Windows.Forms.ToolStripMenuItem("Ayarlar");
        settingsItem.Click += (s, e) => OpenSettings();
        contextMenu.Items.Add(settingsItem);
        
        contextMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Çıkış");
        exitItem.Click += (s, e) => ForceExit();
        contextMenu.Items.Add(exitItem);
        
        _notifyIcon.ContextMenuStrip = contextMenu;
        _notifyIcon.Visible = true;
        
        Log("📌 System tray ikonu hazır");
    }
    
    /// <summary>
    /// Hotkey tetiklendiğinde çağrılır
    /// </summary>
    private void OnHotkeyPressed(object? sender, EventArgs e) {
        Dispatcher.Invoke(() => {
            if (WindowState == WindowState.Minimized || !IsVisible) {
                ShowAndActivate();
            } else {
                // Pencere açıksa minimize et
                MinimizeToTray();
            }
        });
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
        _hotkeyService.UnregisterHotkey();
        
        var settingsWindow = new SettingsWindow(_appSettings, Log);
        settingsWindow.Owner = this;
        settingsWindow.SettingsChanged += OnSettingsChanged;
        settingsWindow.ShowDialog();
        
        // Hotkey'i yeniden kaydet (değişmiş olabilir)
        if (_hotkeyService != null) {
            var modifiers = (GlobalHotkeyService.ModifierKeys)_appSettings.HotkeyModifiers;
            var key = _appSettings.HotkeyKey;
            
            if (_hotkeyService.RegisterHotkey(modifiers, key)) {
                Log($"✅ Global kısayol güncellendi: {_hotkeyService.GetHotkeyString()}");
            } else {
                Log($"⚠️ Global kısayol kaydedilemedi: {_hotkeyService.GetHotkeyString()}");
            }
        }
    }
    
    /// <summary>
    /// Ayarlar değiştiğinde çağrılır
    /// </summary>
    private void OnSettingsChanged(object? sender, AppSettings newSettings) {
        _appSettings = newSettings;
        Log("⚙️ Ayarlar güncellendi");
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

        _applicationLog.MessageWritten -= HandleApplicationLogMessage;
        _indexLifecycle.ProgressChanged -= HandleIndexProgress;
        _indexLifecycle.Error -= HandleIndexError;
        _indexLifecycle.FileChanged -= HandleFileSystemChange;
        _indexLifecycle.ReconciliationProgressChanged -= HandleReconciliationProgress;
        _hotkeyService.HotkeyPressed -= OnHotkeyPressed;
        
        if (_notifyIcon != null) {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }
        
        _internetCheckTimer?.Dispose();
        _internetCheckTimer = null;
        _fileChangeDebounceTimer?.Dispose();
        _fileChangeDebounceTimer = null;
        
        Closing -= MainWindow_Closing;
    }
    
    /// <summary>
    /// İnternet bağlantısını kontrol eder
    /// </summary>
    private bool CheckInternetConnection() {
        try {
            // Hızlı kontrol: Network interface'ler aktif mi?
            if (!NetworkInterface.GetIsNetworkAvailable()) {
                _hasInternetConnection = false;
                UpdateAIButtonState();
                return false;
            }
            
            // DNS ping ile gerçek bağlantı kontrolü
            using var ping = new Ping();
            var reply = ping.Send("8.8.8.8", 1000); // Google DNS, 1 saniye timeout
            _hasInternetConnection = (reply.Status == IPStatus.Success);
        } catch {
            _hasInternetConnection = false;
        }
        
        UpdateAIButtonState();
        return _hasInternetConnection;
    }
    
    /// <summary>
    /// İnternet durumunu periyodik olarak kontrol eder
    /// </summary>
    private void StartInternetMonitoring() {
        _internetCheckTimer = new System.Threading.Timer(_ => {
            Dispatcher.Invoke(() => {
                var wasConnected = _hasInternetConnection;
                CheckInternetConnection();
                
                // Durum değiştiyse log yaz
                if (wasConnected != _hasInternetConnection) {
                    if (_hasInternetConnection) {
                        Log("🌐 İnternet bağlantısı sağlandı");
                    } else {
                        Log("⚠️ İnternet bağlantısı kesildi");
                    }
                }
            });
        }, null, INTERNET_CHECK_INTERVAL_MS, INTERNET_CHECK_INTERVAL_MS);
    }
    
    /// <summary>
    /// AI butonunun durumunu günceller
    /// </summary>
    private void UpdateAIButtonState() {
        NaturalLanguageToggle.IsEnabled = _hasInternetConnection;
        
        // Eğer internet yoksa ve AI modu aktifse, kapat
        if (!_hasInternetConnection && _isNaturalLanguageMode) {
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
    
    private const int MaxConsoleLines = 200; // Maximum log lines before auto-clear
    private int _consoleLineCount = 0;
    
    private void Log(string message) {
        _applicationLog.Write(message);
    }

    private void HandleApplicationLogMessage(string message) {
        if (_isPreparedForShutdown) return;

        if (Dispatcher.CheckAccess()) {
            AppendLogMessage(message);
            return;
        }

        Dispatcher.BeginInvoke(new Action(() => AppendLogMessage(message)));
    }

    private void AppendLogMessage(string message) {
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] {message}\n";
        _consoleLineCount++;

        if (_consoleLineCount > MaxConsoleLines) {
            ConsoleOutput.Text = $"[{timestamp}] 🧹 Konsol otomatik temizlendi ({MaxConsoleLines} satır aşıldı)\n";
            _consoleLineCount = 1;
        }

        ConsoleOutput.Text += logLine;
    }

    private void HandleIndexProgress(IndexProgress progress) {
        if (_isPreparedForShutdown) return;

        Dispatcher.BeginInvoke(new Action(() => {
            LoadingStatus.Text = progress.Status;
            if (progress.Percentage > 0 && progress.Percentage < 100) {
                LoadingProgress.IsIndeterminate = false;
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
    
    private void ToggleConsole() {
        ConsolePanel.Visibility = ConsolePanel.Visibility == Visibility.Visible 
            ? Visibility.Collapsed 
            : Visibility.Visible;
    }
    
    private void ClearConsole() {
        ConsoleOutput.Text = "";
        _consoleLineCount = 0;
        Log("Konsol temizlendi");
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
        // Çift işleme engeli
        if (_isProcessingFileChange) return;
        
        // Dispose old timer
        _fileChangeDebounceTimer?.Dispose();
        
        // Log the change (sadece bir kere)
        Dispatcher.BeginInvoke(() => Log($"📁 {evt.ChangeType}: {System.IO.Path.GetFileName(evt.FullPath)}"));
        
        // Değişikliğin path'ini ve parent'ını sakla
        var changedPath = evt.FullPath;
        var changedParent = System.IO.Path.GetDirectoryName(changedPath);
        
        // Debounce: UI güncellemesini beklet
        _fileChangeDebounceTimer = new System.Threading.Timer(_ =>
        {
            if (_isProcessingFileChange) return;
            _isProcessingFileChange = true;
            
            Dispatcher.InvokeAsync(() =>
            {
                try
                {
                    // Sadece ilgili görünümü güncelle
                    SmartRefreshCurrentView(changedPath, changedParent, evt.ChangeType);
                    
                    // Aktif arama varsa sonuçları da güncelle (sadece arama görünürse)
                    if (!string.IsNullOrWhiteSpace(SearchBox.Text) && 
                        ResultsContainer.Visibility == Visibility.Visible)
                    {
                        _ = RefreshSearchResultsAsync();
                    }
                }
                catch (Exception ex)
                {
                    Log($"⚠️ UI güncelleme hatası: {ex.Message}");
                }
                finally
                {
                    _isProcessingFileChange = false;
                }
            });
        }, null, FILE_CHANGE_DEBOUNCE_MS, Timeout.Infinite);
    }
    
    /// <summary>
    /// Değişikliğin türüne ve konumuna göre akıllıca görünümü günceller.
    /// Ana sayfaya dönmeden sadece ilgili öğeyi günceller.
    /// </summary>
    private void SmartRefreshCurrentView(string changedPath, string? changedParent, SmartFileLauncher.Core.Models.FileChangeType changeType)
    {
        // Eğer bir klasör içindeysek, o klasördeki değişiklikleri kontrol et
        if (_currentFolderPath != null)
        {
            // Değişiklik mevcut klasörde mi?
            if (changedParent != null && 
                string.Equals(changedParent.TrimEnd('\\', '/'), _currentFolderPath.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase))
            {
                // Değişiklik bu klasörde, güncelle
                RefreshCurrentFolderIcons();
            }
            // Değişiklik farklı bir yerdeyse, UI'yı güncelleme (sessizce geç)
            return;
        }
        
        // Ana sayfadayız (_currentFolderPath == null)
        // Sadece root children'daki değişiklikleri güncelle
        var indexedRoots = _indexLifecycle.GetIndexedRoots();
        if (indexedRoots.Count > 0)
        {
            var rootPaths = indexedRoots.Select(c => System.IO.Path.GetDirectoryName(c.FullPath)).Distinct();
            var isInRoot = rootPaths.Any(rp => 
                rp != null && changedParent != null &&
                string.Equals(rp.TrimEnd('\\', '/'), changedParent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            
            if (isInRoot || indexedRoots.Any(c =>
                string.Equals(c.FullPath, changedPath, StringComparison.OrdinalIgnoreCase)))
            {
                RefreshDesktopIconsSmart();
            }
        }
    }
    
    /// <summary>
    /// Mevcut klasördeki ikonları akıllıca günceller (klasör içindeyken).
    /// </summary>
    private void RefreshCurrentFolderIcons()
    {
        if (_currentFolderPath == null || !System.IO.Directory.Exists(_currentFolderPath)) return;
        
        try
        {
            var dirInfo = new System.IO.DirectoryInfo(_currentFolderPath);
            var currentItems = new Dictionary<string, (string Name, bool IsDir)>();
            
            // Mevcut klasördeki öğeleri al
            foreach (var dir in dirInfo.GetDirectories())
            {
                if ((dir.Attributes & System.IO.FileAttributes.Hidden) == 0 &&
                    (dir.Attributes & System.IO.FileAttributes.System) == 0)
                {
                    currentItems[dir.FullName] = (dir.Name, true);
                }
            }
            foreach (var file in dirInfo.GetFiles())
            {
                if ((file.Attributes & System.IO.FileAttributes.Hidden) == 0)
                {
                    currentItems[file.FullName] = (file.Name, false);
                }
            }
            
            // Silinen öğeleri kaldır
            var toRemove = _desktopIcons.Where(d => !currentItems.ContainsKey(d.FullPath)).ToList();
            foreach (var item in toRemove)
            {
                _desktopIcons.Remove(item);
            }
            
            // Yeni öğeleri ekle
            var existingPaths = _desktopIcons.Select(d => d.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
            foreach (var kvp in currentItems.OrderBy(k => k.Value.Name))
            {
                if (!existingPaths.Contains(kvp.Key))
                {
                    var viewModel = new DesktopIconViewModel
                    {
                        Name = kvp.Value.Name,
                        FullPath = kvp.Key,
                        Icon = kvp.Value.IsDir ? "📁" : GetFileIcon(kvp.Value.Name),
                        IsDirectory = kvp.Value.IsDir
                    };
                    
                    if (kvp.Value.IsDir)
                    {
                        viewModel.SetFolderColors(kvp.Value.Name);
                    }
                    
                    // Sıralı ekleme: Önce klasörler, sonra dosyalar (her grup kendi içinde alfabetik)
                    var insertIndex = _desktopIcons.TakeWhile(d => 
                    {
                        // Eğer her ikisi de klasör veya her ikisi de dosya ise alfabetik sırala
                        if (d.IsDirectory == kvp.Value.IsDir)
                        {
                            return string.Compare(d.Name, kvp.Value.Name, StringComparison.OrdinalIgnoreCase) < 0;
                        }
                        // Klasörler her zaman dosyalardan önce
                        return d.IsDirectory;
                    }).Count();
                    _desktopIcons.Insert(insertIndex, viewModel);
                    
                    _ = LoadThumbnailAsync(viewModel);
                }
            }
            
            Log($"🔄 Klasör güncellendi: {_desktopIcons.Count} öğe");
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
                    HasInternetConnection: _hasInternetConnection,
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
                    var ext = System.IO.Path.GetExtension(result.Name).ToLowerInvariant();
                    var isDirectory = string.IsNullOrEmpty(ext) && System.IO.Directory.Exists(result.FullPath);
                    
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
            var tokenizer = new BasicTokenizer();
            var tokens = tokenizer.Tokenize(query).ToList();
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
    private void RetryButton_Click(object sender, RoutedEventArgs e) {
        if (!string.IsNullOrWhiteSpace(_lastSearchQuery)) {
            Log($"🔄 Yeniden deneniyor: '{_lastSearchQuery}'");
            
            // İnternet kontrolü
            CheckInternetConnection();
            
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
                    _hasInternetConnection),
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
                Log($"   Keywords: [{string.Join(", ", structuredQuery.Keywords)}]");
            }

            Log($"   File Types: [{string.Join(", ", structuredQuery.FileTypes)}]");
            if (structuredQuery.PredictedExtensions.Any()) {
                Log($"   🎯 AI Tahmin Edilen Uzantılar: [{string.Join(", ", structuredQuery.PredictedExtensions)}]");
            }

            if (structuredQuery.DateFilter != null) {
                var dateFilter = structuredQuery.DateFilter;
                var parts = new List<string>();
                if (dateFilter.CreatedAfter != null) parts.Add($"Created > {dateFilter.CreatedAfter}");
                if (dateFilter.CreatedBefore != null) parts.Add($"Created < {dateFilter.CreatedBefore}");
                if (dateFilter.ModifiedAfter != null) parts.Add($"Modified > {dateFilter.ModifiedAfter}");
                if (dateFilter.ModifiedBefore != null) parts.Add($"Modified < {dateFilter.ModifiedBefore}");
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
        var tokenizer = new BasicTokenizer();
        foreach (var token in tokenizer.Tokenize(_lastSearchQuery)) {
            var matches = _indexLifecycle.GetTokenMatches(token);
            Log($"   Token '{token}' → indekste {matches.Count} eşleşme");
            if (matches.SampleNames.Count > 0) {
                Log($"      Örnek: {string.Join(", ", matches.SampleNames)}");
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
            var isDirectory = Directory.Exists(result.FullPath);
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
                if (_indexLifecycle.ReconciliationStatus.IsRunning) {
                    await _indexLifecycle.EnsureSyncedAsync(
                        folderPath,
                        _lifetimeCancellation.Token);
                }
                
                // Breadcrumb için klasör yolunu sakla
                _currentFolderPath = folderPath;
                
                // Klasör içeriğini ASYNC yükle (büyük klasörler için optimize edildi)
                if (!await LoadFolderContentsAsync(folderPath)) {
                    return;
                }
                
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
    private async Task<bool> LoadFolderContentsAsync(string folderPath) {
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

        _desktopIcons.Clear();
        EmptyFolderPanel.Visibility = Visibility.Collapsed;

        try {
            var page = await _folderBrowser.LoadAsync(
                folderPath,
                MAX_FOLDER_ITEMS,
                cancellation.Token);
            cancellation.Token.ThrowIfCancellationRequested();
            if (!ReferenceEquals(_folderLoadCancellation, cancellation)) {
                return false;
            }

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
            } else {
                foreach (var item in items) {
                    _desktopIcons.Add(item);
                }

                Log($"   📊 {_desktopIcons.Count} öğe yüklendi" +
                    (page.IsTruncated ? $" (limit: {MAX_FOLDER_ITEMS})" : string.Empty));
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
            // Ana ekrana dön
            GoToHome();
            return;
        }
        
        // Eğer mevcut klasör indekslenen bir kök dizin ise, ana ekrana dön
        bool isRootPath = _indexedRootPaths.Any(root => 
            string.Equals(root.TrimEnd('\\', '/'), _currentFolderPath.TrimEnd('\\', '/'), 
                          StringComparison.OrdinalIgnoreCase));
        
        if (isRootPath) {
            GoToHome();
            return;
        }
        
        var parent = System.IO.Directory.GetParent(_currentFolderPath);
        if (parent != null && parent.Exists) {
            // Parent dizini, indekslenen kök dizinlerden birinin altında mı kontrol et
            bool parentIsInIndexedPath = _indexedRootPaths.Any(root =>
                parent.FullName.StartsWith(root.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase) ||
                string.Equals(root.TrimEnd('\\', '/'), parent.FullName.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            
            if (parentIsInIndexedPath) {
                _ = OpenFolderInApp(parent.FullName);
            } else {
                // Parent indekslenen alanın dışında, ana ekrana dön
                GoToHome();
            }
        } else {
            // Ana ekrana dön
            GoToHome();
        }
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
    
    private string? _selectedItemPath = null;
    private string? _clipboardPath = null;
    private bool _isCutOperation = false;
    private string? _hoveredItemPath = null; // Hover edilen öğenin path'i
    private DesktopIconViewModel? _hoveredItem = null; // Hover edilen öğenin ViewModel'i
    private DesktopIconViewModel? _cutItem = null; // Kesilen öğenin ViewModel'i
    
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
            var directory = Directory.Exists(path)
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
