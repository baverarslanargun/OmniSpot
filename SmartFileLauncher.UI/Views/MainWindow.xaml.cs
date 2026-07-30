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
    private readonly FileSystemScanner _scanner;
    private IndexManager? _indexManager; // Yeni: Akıllı cache sistemi
    private SearchEngine? _searchEngine;
    private AdvancedSearchEngine? _advancedSearchEngine;
    private IntentParser? _intentParser;
    private IThumbnailService? _thumbnailService;
    private InvertedIndex? _index;
    private Dictionary<string, FileMetadata>? _meta;
    private FileSystemNode? _root;
    private string _desktopPath = ""; // Desktop path for icon loading
    private string? _currentFolderPath = null; // Currently browsed folder (null = home/desktop)
    private List<string> _indexedRootPaths = new(); // İndekslenen kök dizinler
    private readonly ObservableCollection<DesktopIconViewModel> _desktopIcons = new();
    private readonly ObservableCollection<SearchResultViewModel> _searchResults = new();
    private bool _isIndexed = false;
    private bool _isNaturalLanguageMode = false;
    private bool _isGridViewMode = false; // Grid görünümü için
    private bool _hasInternetConnection = true; // İnternet bağlantısı durumu
    private bool _useCachedIndex = true; // SQLite cache kullan
    private System.Threading.Timer? _searchDebounceTimer;
    private System.Threading.Timer? _internetCheckTimer;
    private System.Threading.Timer? _fileChangeDebounceTimer; // Dosya değişikliği debounce
    private const int FILE_CHANGE_DEBOUNCE_MS = 1000; // 1 saniye debounce (daha az kasma için artırıldı)
    private bool _isProcessingFileChange = false; // Çift işleme engeli
    private CancellationTokenSource? _currentSearchCancellation;
    private string _lastSearchQuery = ""; // Son arama sorgusu (retry için)
    private const int DEBOUNCE_DELAY_MS = 1200; // 1.2 seconds delay after last keystroke (increased from 400ms)
    private const int THUMBNAIL_SIZE = 128; // Thumbnail boyutu
    private const int INTERNET_CHECK_INTERVAL_MS = 10000; // 10 saniyede bir kontrol
    
    // Global Hotkey ve Ayarlar
    private GlobalHotkeyService? _hotkeyService;
    private AppSettings _appSettings = null!;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    
    public MainWindow() {
        InitializeComponent();
        
        // Ayarları yükle
        _appSettings = AppSettings.Load();
        
        // Initialize scanner
        var tokenizer = new BasicTokenizer();
        _scanner = new FileSystemScanner(tokenizer);
        
        // Initialize thumbnail service
        _thumbnailService = new ThumbnailService(Log);
        
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
        // Cache ayarını uygula
        _useCachedIndex = _appSettings.UseCachedIndex;
        
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
        _hotkeyService = new GlobalHotkeyService();
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
        _hotkeyService?.UnregisterHotkey();
        
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
        // IndexManager'ı kapat (cache'i kaydet)
        _indexManager?.Dispose();
        
        // Hotkey'i kaldır
        _hotkeyService?.Dispose();
        
        // System tray ikonunu kaldır
        if (_notifyIcon != null) {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
        }
        
        // Timer'ları temizle
        _searchDebounceTimer?.Dispose();
        _internetCheckTimer?.Dispose();
        _currentSearchCancellation?.Dispose();
        
        // Kapatma olayını bypass et
        Closing -= MainWindow_Closing;
        
        System.Windows.Application.Current.Shutdown();
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
        var timestamp = DateTime.Now.ToString("HH:mm:ss.fff");
        var logLine = $"[{timestamp}] {message}\n";
        Dispatcher.Invoke(() => {
            _consoleLineCount++;
            
            // Auto-clear when too many lines to prevent memory issues
            if (_consoleLineCount > MaxConsoleLines) {
                ConsoleOutput.Text = $"[{timestamp}] 🧹 Konsol otomatik temizlendi ({MaxConsoleLines} satır aşıldı)\n";
                _consoleLineCount = 1;
            }
            
            ConsoleOutput.Text += logLine;
        });
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
            
            // Kullanıcı profil yolunu al
            string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
            
            // Taranacak dizinleri belirle
            var pathsToIndex = new List<string>();
            
            // Desktop (OneDrive desteği ile)
            string desktop = Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
            if (string.IsNullOrEmpty(desktop) || !System.IO.Directory.Exists(desktop)) {
                string oneDriveDesktop = System.IO.Path.Combine(userProfile, "OneDrive", "Masaüstü");
                if (System.IO.Directory.Exists(oneDriveDesktop)) {
                    desktop = oneDriveDesktop;
                } else {
                    oneDriveDesktop = System.IO.Path.Combine(userProfile, "OneDrive", "Desktop");
                    if (System.IO.Directory.Exists(oneDriveDesktop)) {
                        desktop = oneDriveDesktop;
                    } else {
                        desktop = System.IO.Path.Combine(userProfile, "Desktop");
                    }
                }
            }
            if (System.IO.Directory.Exists(desktop)) {
                pathsToIndex.Add(desktop);
                Log($"📂 Desktop: {desktop}");
            }
            
            // Documents
            string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            if (!string.IsNullOrEmpty(documents) && System.IO.Directory.Exists(documents)) {
                pathsToIndex.Add(documents);
                Log($"📂 Documents: {documents}");
            }
            
            // Downloads
            string downloads = System.IO.Path.Combine(userProfile, "Downloads");
            if (System.IO.Directory.Exists(downloads)) {
                pathsToIndex.Add(downloads);
                Log($"📂 Downloads: {downloads}");
            }
            
            // Pictures
            string pictures = Environment.GetFolderPath(Environment.SpecialFolder.MyPictures);
            if (!string.IsNullOrEmpty(pictures) && System.IO.Directory.Exists(pictures)) {
                pathsToIndex.Add(pictures);
                Log($"📂 Pictures: {pictures}");
            }
            
            // Music
            string music = Environment.GetFolderPath(Environment.SpecialFolder.MyMusic);
            if (!string.IsNullOrEmpty(music) && System.IO.Directory.Exists(music)) {
                pathsToIndex.Add(music);
                Log($"📂 Music: {music}");
            }
            
            // Videos
            string videos = Environment.GetFolderPath(Environment.SpecialFolder.MyVideos);
            if (!string.IsNullOrEmpty(videos) && System.IO.Directory.Exists(videos)) {
                pathsToIndex.Add(videos);
                Log($"📂 Videos: {videos}");
            }
            
            Log($"📊 Toplam {pathsToIndex.Count} dizin taranacak");
            
            // Kök dizinleri sakla (navigasyon için)
            _indexedRootPaths = pathsToIndex;
            
            if (_useCachedIndex) {
                // Yeni: IndexManager ile akıllı cache
                await InitializeWithCacheAsync(pathsToIndex, desktop);
            } else {
                // Eski: Sıfırdan tarama
                await InitializeWithFullScanAsync();
            }
            
        } catch (Exception ex) {
            Log($"❌ HATA: {ex.Message}");
            Log($"Stack trace: {ex.StackTrace}");
            await Dispatcher.InvokeAsync(() => {
                LoadingStatus.Text = $"Hata: {ex.Message}";
                LoadingProgress.IsIndeterminate = false;
                System.Windows.MessageBox.Show($"İndeksleme başarısız: {ex.Message}\n\nDetaylar için konsolu kontrol edin.", "Hata", 
                    MessageBoxButton.OK, MessageBoxImage.Error);
            });
        }
    }
    
    /// <summary>
    /// Yeni: IndexManager ile akıllı cache kullanarak başlat
    /// </summary>
    private async Task InitializeWithCacheAsync(List<string> pathsToIndex, string desktopPath) {
        var sw = Stopwatch.StartNew();
        
        // IndexManager oluştur
        _indexManager = new IndexManager(new BasicTokenizer());
        _indexManager.OnProgress += progress => {
            Dispatcher.Invoke(() => {
                LoadingStatus.Text = progress.Status;
                if (progress.Percentage > 0 && progress.Percentage < 100) {
                    LoadingProgress.IsIndeterminate = false;
                    LoadingProgress.Value = progress.Percentage;
                }
            });
        };
        _indexManager.OnError += error => Log($"⚠️ {error}");
        _indexManager.OnFileChange += HandleFileSystemChange;
        
        // Delta sync progress handler
        _indexManager.OnDeltaSyncProgress += (processed, total, percentage) => {
            Dispatcher.BeginInvoke(() => {
                UpdateDeltaSyncProgress(processed, total, percentage);
            });
        };
        
        Log($"📦 Database: {_indexManager.DatabasePath}");
        
        // Initialize with all paths (cache varsa yükler, yoksa tarar)
        await _indexManager.InitializeAsync(pathsToIndex);
        
        // IndexManager'dan veri yapılarını al
        _index = _indexManager.InvertedIndex;
        _meta = _indexManager.MetadataMap;
        _root = _indexManager.RootNode;
        
        // Desktop path'i sakla (icon yükleme için)
        _desktopPath = desktopPath;
        
        sw.Stop();
        
        // UI güncelle
        await Dispatcher.InvokeAsync(() => {
            var stats = _indexManager.GetStats();
            LoadingStatus.Text = $"{stats.FileCount} dosya, {stats.DirectoryCount} klasör indekslendi";
            Log($"✅ İndeksleme tamamlandı ({sw.ElapsedMilliseconds}ms)");
            Log($"   📄 Dosya sayısı: {stats.FileCount}");
            Log($"   📁 Klasör sayısı: {stats.DirectoryCount}");
            Log($"   🔤 Token sayısı: {stats.TokenCount}");
            if (stats.LastScanTime.HasValue) {
                Log($"   🕐 Son tarama: {stats.LastScanTime.Value:g}");
            }
            
            // Search engine'leri oluştur
            _searchEngine = new SearchEngine(_index!, new BasicTokenizer(), new BasicScoringStrategy());
            _advancedSearchEngine = new AdvancedSearchEngine(_index!, new BasicTokenizer(), new BasicScoringStrategy(), _root);
            
            // Intent parser
            try {
                _intentParser = new IntentParser(Log);
                Log("✅ Rule-based intent parser hazır");
            } catch (Exception ex) {
                Log($"⚠️ Intent parser yüklenemedi: {ex.Message}");
            }
            
            LoadDesktopIcons();
            _isIndexed = true;
            
            Log("✅ Arama motoru hazır");
            
            // Show delta sync progress if running
            if (_indexManager.IsDeltaSyncRunning) {
                Log("� Arka planda delta sync devam ediyor...");
                DeltaSyncPanel.Visibility = Visibility.Visible;
            } else {
                Log("�💡 FileSystemWatcher aktif - değişiklikler otomatik izleniyor");
            }
            
            // Hide loading, show content
            LoadingOverlay.Visibility = Visibility.Collapsed;
            DesktopIconsScroll.Visibility = Visibility.Visible;
            SearchBox.Focus();
        });
    }
    
    /// <summary>
    /// Eski: Sıfırdan tam tarama (fallback)
    /// </summary>
    private async Task InitializeWithFullScanAsync() {
        Log("Desktop taraması başlatılıyor (cache devre dışı)...");
        LoadingStatus.Text = "Desktop taranıyor...";
        
        await Task.Run(() => {
            _root = _scanner.ScanDesktop(out var index, out var meta);
            _index = index;
            _meta = meta;
        });
        
        await Dispatcher.InvokeAsync(() => {
            var fileCount = _root!.Children.Count;
            LoadingStatus.Text = $"{fileCount} öğe bulundu";
            Log($"✅ Tarama tamamlandı: {fileCount} öğe bulundu");
            Log($"📂 Taranan yol: {_root.FullPath}");
            
            _searchEngine = new SearchEngine(_index!, new BasicTokenizer(), new BasicScoringStrategy());
            _advancedSearchEngine = new AdvancedSearchEngine(_index!, new BasicTokenizer(), new BasicScoringStrategy(), _root);
            
            try {
                _intentParser = new IntentParser(Log);
                Log("✅ Rule-based intent parser hazır");
            } catch (Exception ex) {
                Log($"⚠️ Intent parser yüklenemedi: {ex.Message}");
            }
            
            LoadDesktopIcons();
            _isIndexed = true;
            
            Log("✅ İndeksleme tamamlandı, arama motoru hazır");
            
            LoadingOverlay.Visibility = Visibility.Collapsed;
            DesktopIconsScroll.Visibility = Visibility.Visible;
            SearchBox.Focus();
        });
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
        if (_root != null)
        {
            var rootPaths = _root.Children.Select(c => System.IO.Path.GetDirectoryName(c.FullPath)).Distinct();
            var isInRoot = rootPaths.Any(rp => 
                rp != null && changedParent != null &&
                string.Equals(rp.TrimEnd('\\', '/'), changedParent.TrimEnd('\\', '/'), StringComparison.OrdinalIgnoreCase));
            
            if (isInRoot || _root.Children.Any(c => 
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
        if (_root == null || _thumbnailService == null) return;
        
        // Mevcut öğelerin path'lerini al
        var existingPaths = _desktopIcons.ToDictionary(d => d.FullPath, d => d, StringComparer.OrdinalIgnoreCase);
        var currentPaths = _root.Children.Select(c => c.FullPath).ToHashSet(StringComparer.OrdinalIgnoreCase);
        
        // Silinen öğeleri kaldır
        var toRemove = _desktopIcons.Where(d => !currentPaths.Contains(d.FullPath)).ToList();
        foreach (var item in toRemove)
        {
            _desktopIcons.Remove(item);
        }
        
        // Yeni öğeleri ekle
        foreach (var child in _root.Children.OrderBy(n => n.Name))
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
        if (string.IsNullOrWhiteSpace(_lastSearchQuery) || _searchEngine == null) return;
        
        try
        {
            // Mevcut CancellationToken'ı iptal etme - sadece sonuçları güncelle
            var results = _searchEngine.Search(_lastSearchQuery);
            
            await Dispatcher.InvokeAsync(() =>
            {
                _searchResults.Clear();
                foreach (var result in results.Take(50))
                {
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
        catch (Exception ex)
        {
            Log($"⚠️ Arama güncelleme hatası: {ex.Message}");
        }
    }
    
    private void LoadDesktopIcons() {
        _desktopIcons.Clear();
        if (_root == null || _thumbnailService == null) return;
        
        Log($"📸 Thumbnail yükleme başladı... ({_root.Children.Count} öğe)");
        
        // Önce klasörler, sonra dosyalar - her grup alfabetik sıralı
        var sortedChildren = _root.Children
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
            var thumbnail = await _thumbnailService!.GetThumbnailAsync(
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
            var thumbnail = await _thumbnailService!.GetThumbnailAsync(
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
            // Cancel any pending search and active search
            _searchDebounceTimer?.Dispose();
            _searchDebounceTimer = null;
            
            // Cancel and dispose safely
            try {
                _currentSearchCancellation?.Cancel();
            } catch { /* Ignore cancellation errors */ }
            
            try {
                _currentSearchCancellation?.Dispose();
            } catch { /* Ignore disposal errors */ }
            _currentSearchCancellation = null;
            
            // Show desktop icons, hide search results
            DesktopIconsScroll.Visibility = Visibility.Visible;
            ResultsContainer.Visibility = Visibility.Collapsed;
            _searchResults.Clear();
        } else {
            // Cancel previous pending search
            _searchDebounceTimer?.Dispose();
            _searchDebounceTimer = null;
            
            // Capture query for closure
            var capturedQuery = query;
            
            // Debounce search - wait for user to stop typing
            _searchDebounceTimer = new System.Threading.Timer(_ => {
                Dispatcher.Invoke(async () => {
                    // Cancel any ongoing search safely
                    try {
                        _currentSearchCancellation?.Cancel();
                    } catch { /* Ignore cancellation errors */ }
                    
                    try {
                        _currentSearchCancellation?.Dispose();
                    } catch { /* Ignore disposal errors */ }
                    
                    // Create new cancellation token source
                    _currentSearchCancellation = new CancellationTokenSource();
                    var token = _currentSearchCancellation.Token;
                    
                    // Show search results, hide desktop icons
                    DesktopIconsScroll.Visibility = Visibility.Collapsed;
                    ResultsContainer.Visibility = Visibility.Visible;
                    
                    Log($"🔍 Arama sorgusu: '{capturedQuery}'");
                    
                    // Son sorguyu sakla (retry için)
                    _lastSearchQuery = capturedQuery;
                    
                    // Debug: show tokenization
                    var tokenizer = new BasicTokenizer();
                    var tokens = tokenizer.Tokenize(capturedQuery).ToList();
                    Log($"🔤 Query tokenler: [{string.Join(", ", tokens)}]");
                    
                    // Aranıyor göstergesini göster
                    ShowSearchingIndicator(capturedQuery);
                    
                    try {
                        await RunSearchAsync(capturedQuery, token);
                    } catch (OperationCanceledException) {
                        Log("🚫 Arama iptal edildi (yeni sorgu başlatıldı)");
                        HideAllPanels();
                    } catch (Exception ex) {
                        Log($"❌ Arama exception: {ex.Message}");
                        ShowError("Arama sırasında bir hata oluştu", ex.Message);
                    }
                });
            }, null, DEBOUNCE_DELAY_MS, Timeout.Infinite);
        }
    }
    
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
            ShowSearchingIndicator(_lastSearchQuery);
            
            _currentSearchCancellation?.Cancel();
            _currentSearchCancellation?.Dispose();
            _currentSearchCancellation = new CancellationTokenSource();
            
            _ = Task.Run(async () => {
                try {
                    await Dispatcher.InvokeAsync(async () => {
                        await RunSearchAsync(_lastSearchQuery, _currentSearchCancellation.Token);
                    });
                } catch (Exception ex) {
                    await Dispatcher.InvokeAsync(() => {
                        ShowError("Arama sırasında bir hata oluştu", ex.Message);
                    });
                }
            });
        }
    }
    
    private async Task RunSearchAsync(string query, CancellationToken cancellationToken = default) {
        if (_searchEngine == null) {
            Log("HATA: SearchEngine null!");
            ShowError("Arama motoru başlatılamadı", "Uygulama düzgün yüklenmemiş olabilir. Lütfen uygulamayı yeniden başlatın.");
            return;
        }
        
        // Check if delta sync is still running
        if (_indexManager != null && _indexManager.IsDeltaSyncRunning) {
            var progress = _indexManager.DeltaSyncProgress;
            var processed = _indexManager.DeltaSyncProcessed;
            var total = _indexManager.DeltaSyncTotal;
            ShowDeltaSyncWarning($"%{progress} tamamlandı ({processed:N0}/{total:N0} dosya kontrol edildi)");
        } else {
            DeltaSyncWarningBanner.Visibility = Visibility.Collapsed;
        }
        
        List<SearchResult> resultList;
        
        try {
            // Check cancellation before starting
            cancellationToken.ThrowIfCancellationRequested();
            
            // Fallback durumunu takip et
            bool usedFallback = false;
            string? fallbackReason = null;
            string? warningMessage = null;
            
            if (_isNaturalLanguageMode && _intentParser != null && _advancedSearchEngine != null) {
                // Natural language mode - use intent parser
                Log("🤖 Doğal dil işleniyor...");
                
                // İnternet kontrolü
                if (!_hasInternetConnection) {
                    Log("⚠️ İnternet bağlantısı yok, rule-based aramaya geçiliyor");
                    usedFallback = true;
                    fallbackReason = "İnternet bağlantısı yok";
                    // Standart aramaya geç
                    resultList = _searchEngine.Search(query, 100).ToList();
                } else {
                    // Run AI parsing (Groq) with timeout protection
                    StructuredQuery? structuredQuery = null;
                    try {
                        structuredQuery = await _intentParser.ParseWithGroqAsync(query, cancellationToken);
                        
                        // Fallback durumunu kontrol et
                        if (structuredQuery.UsedFallback) {
                            usedFallback = true;
                            fallbackReason = structuredQuery.FallbackReason;
                        }
                        
                        // Kısmi hata uyarısını kontrol et (örn: Keyword API başarısız ama Intent başarılı)
                        if (!string.IsNullOrEmpty(structuredQuery.WarningMessage)) {
                            warningMessage = structuredQuery.WarningMessage;
                        }
                    } catch (OperationCanceledException) {
                        throw; // Re-throw cancellation
                    } catch (Exception ex) {
                        Log($"⚠️ Groq API hatası, rule-based'e geçiliyor: {ex.Message}");
                        usedFallback = true;
                        fallbackReason = ex.Message;
                        // Fallback to rule-based parsing
                        structuredQuery = _intentParser.ParseIntent(query);
                        structuredQuery.UsedFallback = true;
                        structuredQuery.FallbackReason = fallbackReason;
                    }
                    
                    // Check if cancelled after LLM inference
                    cancellationToken.ThrowIfCancellationRequested();
                    
                    if (structuredQuery == null) {
                        Log("⚠️ StructuredQuery null, varsayılan kullanılıyor");
                        usedFallback = true;
                        fallbackReason = "Parser sonucu boş";
                        structuredQuery = new StructuredQuery {
                            Intent = "search_files",
                            Keywords = new List<string> { query },
                            FileTypes = new List<string>(),
                            PredictedExtensions = new List<string>(),
                            IncludeFolderContents = true,
                            UsedFallback = true,
                            FallbackReason = fallbackReason
                        };
                    }
                    
                    // Log parsed intent
                    if (usedFallback) {
                        Log($"📋 Rule-based arama (fallback):");
                    } else {
                        Log($"📋 AI destekli arama:");
                    }
                    Log($"   Intent: {structuredQuery.Intent}");
                    
                    // Show filter-only mode status
                    if (structuredQuery.FilterOnlyMode) {
                        Log($"   🔍 Mode: FILTER-ONLY (sadece filtrelerle arama)");
                    } else {
                        Log($"   Keywords: [{string.Join(", ", structuredQuery.Keywords)}]");
                    }
                    
                    Log($"   File Types: [{string.Join(", ", structuredQuery.FileTypes)}]");
                    
                    // AI-powered extension prediction
                    if (structuredQuery.PredictedExtensions.Any()) {
                        Log($"   🎯 AI Tahmin Edilen Uzantılar: [{string.Join(", ", structuredQuery.PredictedExtensions)}]");
                    }
                    
                    if (structuredQuery.DateFilter != null) {
                        var df = structuredQuery.DateFilter;
                        var parts = new List<string>();
                        if (df.CreatedAfter != null) parts.Add($"Created > {df.CreatedAfter}");
                        if (df.CreatedBefore != null) parts.Add($"Created < {df.CreatedBefore}");
                        if (df.ModifiedAfter != null) parts.Add($"Modified > {df.ModifiedAfter}");
                        if (df.ModifiedBefore != null) parts.Add($"Modified < {df.ModifiedBefore}");
                        
                        if (parts.Any()) {
                            Log($"   📅 Date Filter: {string.Join(", ", parts)}");
                        }
                    }
                    if (structuredQuery.SizeFilter != null) {
                        Log($"   Size Filter: {structuredQuery.SizeFilter.MinMb}MB - {structuredQuery.SizeFilter.MaxMb}MB");
                    }
                    if (structuredQuery.FolderHints.Any()) {
                        Log($"   Folder Hints: [{string.Join(", ", structuredQuery.FolderHints.Select(h => h.Name))}]");
                    }
                    Log($"   Include Folders: {structuredQuery.IncludeFolderContents}");
                    
                    // Execute search and materialize results immediately
                    resultList = _advancedSearchEngine.Search(structuredQuery, 100).ToList();

                    // Handle Auto-Open Action
                    if (structuredQuery.OpenAction != null && structuredQuery.OpenAction.ShouldOpen && resultList.Any()) {
                        var bestMatch = resultList.First();
                        
                        if (structuredQuery.OpenAction.OpenMode == "single_best") {
                            // Directly open the best match, but still show results
                            Log($"🚀 Auto-opening best match: {bestMatch.Name}");
                            
                            await Dispatcher.InvokeAsync(() => {
                                OpenFile(bestMatch.FullPath);
                            });
                            
                            // Continue to show results - don't return early
                        } else {
                            // "show_list" mode - just highlight the best match
                            Log($"🎯 Best match highlighted: {bestMatch.Name}");
                        }
                    }
                }
            } else {
                // Standard keyword search - materialize immediately
                resultList = _searchEngine.Search(query, 100).ToList();
            }
            
            // Check cancellation before updating UI
            cancellationToken.ThrowIfCancellationRequested();
            Log($"✅ Sonuç sayısı: {resultList.Count}");
            
            // Fallback veya Warning uyarısını göster (AI modu aktifse)
            if (_isNaturalLanguageMode && usedFallback && !string.IsNullOrEmpty(fallbackReason)) {
                ShowFallbackWarning(fallbackReason);
            } else if (_isNaturalLanguageMode && !string.IsNullOrEmpty(warningMessage)) {
                // Kısmi hata durumu - Keyword API başarısız ama Intent başarılı
                ShowFallbackWarning(warningMessage);
            } else {
                FallbackWarningBanner.Visibility = Visibility.Collapsed;
            }
            
            if (resultList.Any()) {
                var topResults = resultList.Take(5).Select(r => $"   • {r.Name} (skor: {r.Score:F0})");
                Log($"🏆 İlk {Math.Min(5, resultList.Count)} sonuç:");
                foreach (var r in topResults) {
                    Log(r);
                }
            } else {
                Log("⚠️ Hiç sonuç bulunamadı!");
                
                // Debug: Check if index has data
                if (_index != null) {
                    Log("🔍 İndeks durumu kontrol ediliyor...");
                    var tokenizer = new BasicTokenizer();
                    var queryTokens = tokenizer.Tokenize(query).ToList();
                    foreach (var token in queryTokens) {
                        var indexResults = _index.Get(token);
                        Log($"   Token '{token}' → indekste {indexResults.Count} eşleşme");
                        if (indexResults.Count > 0) {
                            var samples = indexResults.Take(3).Select(n => n.Name);
                            Log($"      Örnek: {string.Join(", ", samples)}");
                        }
                    }
                }
            }
            
            // Aranıyor göstergesini gizle
            SearchingPanel.Visibility = Visibility.Collapsed;
            ErrorPanel.Visibility = Visibility.Collapsed;
            
            // SearchResult -> SearchResultViewModel dönüşümü ve thumbnail yükleme
            _searchResults.Clear();
            
            if (resultList.Count == 0)
            {
                // Sonuç yok - NoResultsPanel göster
                NoResultsPanel.Visibility = Visibility.Visible;
                ResultsList.Visibility = Visibility.Collapsed;
                ResultsGridScroll.Visibility = Visibility.Collapsed;
            }
            else
            {
                // Sonuçlar var - liste/grid göster
                NoResultsPanel.Visibility = Visibility.Collapsed;
                if (_isGridViewMode)
                {
                    ResultsList.Visibility = Visibility.Collapsed;
                    ResultsGridScroll.Visibility = Visibility.Visible;
                }
                else
                {
                    ResultsList.Visibility = Visibility.Visible;
                    ResultsGridScroll.Visibility = Visibility.Collapsed;
                }
                
                foreach (var result in resultList)
                {
                    var isDirectory = System.IO.Directory.Exists(result.FullPath);
                    
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
                    
                    // Async thumbnail yükleme
                    _ = LoadSearchResultThumbnailAsync(viewModel);
                }
            }
            
        } catch (OperationCanceledException) {
            // Arama iptal edildi - sessizce çık, hata değil
            Log("🚫 Arama iptal edildi");
            HideAllPanels();
        } catch (Exception ex) {
            Log($"❌ Arama hatası: {ex.Message}");
            Log($"Stack: {ex.StackTrace}");
            ShowError("Arama sırasında bir hata oluştu", ex.Message);
        }
    }
    
    private async Task LoadSearchResultThumbnailAsync(SearchResultViewModel viewModel)
    {
        try
        {
            if (_thumbnailService == null) return;
            
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
            Shell32Helper.ShowProperties(targetPath);
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
        
        var targetFolder = _currentFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        var fileName = Path.GetFileName(_clipboardPath);
        var destPath = Path.Combine(targetFolder, fileName);
        
        // Aynı isimde dosya varsa yeni isim oluştur
        destPath = GetUniqueFilePath(destPath);
        
        try {
            if (Directory.Exists(_clipboardPath)) {
                if (_isCutOperation) {
                    Directory.Move(_clipboardPath, destPath);
                    ShowFeedback($"📁 Taşındı: {Path.GetFileName(destPath)}");
                } else {
                    CopyDirectory(_clipboardPath, destPath);
                    ShowFeedback($"📁 Yapıştırıldı: {Path.GetFileName(destPath)}");
                }
            } else if (File.Exists(_clipboardPath)) {
                if (_isCutOperation) {
                    File.Move(_clipboardPath, destPath);
                    ShowFeedback($"📄 Taşındı: {Path.GetFileName(destPath)}");
                } else {
                    File.Copy(_clipboardPath, destPath);
                    ShowFeedback($"📄 Yapıştırıldı: {Path.GetFileName(destPath)}");
                }
            }
            
            // Kesme işleminden sonra temizle
            if (_isCutOperation) {
                // Kesilen öğeyi görünümden kaldır
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
            var directory = Path.GetDirectoryName(path);
            var newPath = Path.Combine(directory ?? "", newName);
            
            try {
                if (Directory.Exists(path)) {
                    Directory.Move(path, newPath);
                    ShowFeedback($"📁 Adlandırıldı: {currentName} → {newName}");
                } else if (File.Exists(path)) {
                    File.Move(path, newPath);
                    ShowFeedback($"📄 Adlandırıldı: {currentName} → {newName}");
                }
                UpdateItemInView(path, newPath, newName);
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
                if (Directory.Exists(path)) {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteDirectory(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
                    ShowFeedback($"🗑️ Silindi: {name}");
                } else if (File.Exists(path)) {
                    Microsoft.VisualBasic.FileIO.FileSystem.DeleteFile(
                        path,
                        Microsoft.VisualBasic.FileIO.UIOption.OnlyErrorDialogs,
                        Microsoft.VisualBasic.FileIO.RecycleOption.SendToRecycleBin);
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
            // Klasör mü kontrol et
            if (System.IO.Directory.Exists(path)) {
                // Klasörü uygulama içinde aç
                OpenFolderInApp(path);
            } else {
                // Dosyayı sistem ile aç
                Process.Start(new ProcessStartInfo(path) { UseShellExecute = true });
                if (_indexManager != null) {
                    _indexManager.IncrementOpenCount(path);
                } else if (_meta != null && _meta.TryGetValue(path, out var meta)) {
                    meta.OpenCount++;
                }
            }
        } catch (Exception ex) {
            System.Windows.MessageBox.Show($"Açılamadı: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
    }
    
    /// <summary>
    /// Klasörü uygulama içinde açar ve içeriğini gösterir
    /// </summary>
    private async void OpenFolderInApp(string folderPath) {
        try {
            Log($"📂 Klasör açılıyor: {folderPath}");
            
            // Show loading indicator
            ShowFolderLoadingIndicator(folderPath);
            
            try {
                // Check if delta sync is running and this folder isn't synced yet
                if (_indexManager != null && _indexManager.IsDeltaSyncRunning) {
                    // Ensure folder is synced (on-demand sync if needed)
                    await _indexManager.EnsureSyncedAsync(folderPath);
                }
                
                // Breadcrumb için klasör yolunu sakla
                _currentFolderPath = folderPath;
                
                // Klasör içeriğini ASYNC yükle (büyük klasörler için optimize edildi)
                await LoadFolderContentsAsync(folderPath);
                
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
    private async Task LoadFolderContentsAsync(string folderPath) {
        // Clear on UI thread first
        _desktopIcons.Clear();
        
        // Hide empty folder message initially
        EmptyFolderPanel.Visibility = Visibility.Collapsed;
        
        try {
            // Load items in background thread
            var items = await Task.Run(() => {
                var result = new List<DesktopIconViewModel>();
                var dirInfo = new System.IO.DirectoryInfo(folderPath);
                int count = 0;
                
                // Önce klasörleri ekle (EnumerateDirectories - streaming)
                foreach (var dir in dirInfo.EnumerateDirectories().OrderBy(d => d.Name)) {
                    try {
                        // Limit kontrolü
                        if (count >= MAX_FOLDER_ITEMS) break;
                        
                        // Gizli ve sistem klasörlerini atla
                        if ((dir.Attributes & System.IO.FileAttributes.Hidden) != 0 ||
                            (dir.Attributes & System.IO.FileAttributes.System) != 0) continue;
                        
                        var icon = new DesktopIconViewModel {
                            Name = dir.Name,
                            FullPath = dir.FullName,
                            Icon = "📁",
                            IsDirectory = true
                        };
                        
                        // Klasör rengini ayarla
                        icon.SetFolderColors(dir.Name);
                        
                        result.Add(icon);
                        count++;
                    } catch { }
                }
                
                // Sonra dosyaları ekle (EnumerateFiles - streaming)
                foreach (var file in dirInfo.EnumerateFiles().OrderBy(f => f.Name)) {
                    try {
                        // Limit kontrolü
                        if (count >= MAX_FOLDER_ITEMS) break;
                        
                        // Gizli dosyaları atla
                        if ((file.Attributes & System.IO.FileAttributes.Hidden) != 0) continue;
                        
                        var icon = new DesktopIconViewModel {
                            Name = file.Name,
                            FullPath = file.FullName,
                            Icon = GetFileIcon(file.Extension),
                            IsDirectory = false
                        };
                        result.Add(icon);
                        count++;
                    } catch { }
                }
                
                return result;
            });
            
            // Check if folder is empty
            if (items.Count == 0) {
                // Show empty folder message
                var folderName = System.IO.Path.GetFileName(folderPath);
                if (string.IsNullOrEmpty(folderName)) folderName = folderPath;
                
                EmptyFolderTitle.Text = $"'{folderName}' klasörü boş";
                EmptyFolderPanel.Visibility = Visibility.Visible;
                
                Log($"   📂 Klasör boş");
            } else {
                // Add all items at once (batched update - much faster than individual adds)
                foreach (var item in items) {
                    _desktopIcons.Add(item);
                }
                
                Log($"   📊 {_desktopIcons.Count} öğe yüklendi" + 
                    (_desktopIcons.Count >= MAX_FOLDER_ITEMS ? $" (limit: {MAX_FOLDER_ITEMS})" : ""));
                
                // Load thumbnails in background batches (don't block UI)
                _ = LoadThumbnailsInBatchesAsync(items);
            }
            
        } catch (Exception ex) {
            Log($"❌ Klasör içeriği yüklenemedi: {ex.Message}");
        }
    }
    
    /// <summary>
    /// Thumbnail'leri batch'ler halinde yükler (UI'ı bloklamaz)
    /// </summary>
    private async Task LoadThumbnailsInBatchesAsync(List<DesktopIconViewModel> items) {
        if (_thumbnailService == null) return;
        
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
        if (_thumbnailService == null) return;
        
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
                OpenFolderInApp(parent.FullName);
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
            // Windows "Birlikte Aç" dialogunu aç
            var psi = new ProcessStartInfo {
                FileName = "rundll32.exe",
                Arguments = $"shell32.dll,OpenAs_RunDLL {path}",
                UseShellExecute = true
            };
            Process.Start(psi);
            Log($"🔗 Birlikte aç: {path}");
        } catch (Exception ex) {
            Log($"❌ Birlikte aç hatası: {ex.Message}");
            System.Windows.MessageBox.Show($"Birlikte aç hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
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
                // Dosya Gezgini'nde aç ve dosyayı seç
                Process.Start("explorer.exe", $"/select,\"{path}\"");
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
            // Windows özellikler penceresini aç
            var psi = new ProcessStartInfo {
                FileName = "explorer.exe",
                Arguments = $"/select,\"{path}\"",
                UseShellExecute = true
            };
            
            // Shell'in properties komutunu kullan
            var info = new System.Diagnostics.ProcessStartInfo("explorer.exe") {
                Arguments = $"shell:::{{{Guid.NewGuid()}}}"
            };
            
            // Alternatif: verb kullan
            Shell32Helper.ShowProperties(path);
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
        var targetFolder = _currentFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        
        var dialog = new RenameDialog("Yeni Klasör", isNew: true);
        dialog.Owner = this;
        
        if (dialog.ShowDialog() == true) {
            var folderPath = Path.Combine(targetFolder, dialog.NewName);
            folderPath = GetUniqueFilePath(folderPath);
            
            try {
                Directory.CreateDirectory(folderPath);
                Log($"📁 Yeni klasör oluşturuldu: {dialog.NewName}");
                RefreshCurrentFolder();
            } catch (Exception ex) {
                Log($"❌ Klasör oluşturma hatası: {ex.Message}");
                System.Windows.MessageBox.Show($"Klasör oluşturma hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
        }
    }
    
    private void ContextMenu_NewTextFile(object sender, RoutedEventArgs e) {
        var targetFolder = _currentFolderPath ?? Environment.GetFolderPath(Environment.SpecialFolder.Desktop);
        
        var dialog = new RenameDialog("Yeni Metin Belgesi.txt", isNew: true);
        dialog.Owner = this;
        
        if (dialog.ShowDialog() == true) {
            var filePath = Path.Combine(targetFolder, dialog.NewName);
            filePath = GetUniqueFilePath(filePath);
            
            try {
                File.WriteAllText(filePath, "");
                Log($"📄 Yeni dosya oluşturuldu: {dialog.NewName}");
                RefreshCurrentFolder();
            } catch (Exception ex) {
                Log($"❌ Dosya oluşturma hatası: {ex.Message}");
                System.Windows.MessageBox.Show($"Dosya oluşturma hatası: {ex.Message}", "Hata", MessageBoxButton.OK, MessageBoxImage.Error);
            }
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
    
    private string GetUniqueFilePath(string path) {
        if (!File.Exists(path) && !Directory.Exists(path)) {
            return path;
        }
        
        var directory = Path.GetDirectoryName(path) ?? "";
        var nameWithoutExt = Path.GetFileNameWithoutExtension(path);
        var extension = Path.GetExtension(path);
        
        int counter = 1;
        string newPath;
        do {
            newPath = Path.Combine(directory, $"{nameWithoutExt} ({counter}){extension}");
            counter++;
        } while (File.Exists(newPath) || Directory.Exists(newPath));
        
        return newPath;
    }
    
    private void CopyDirectory(string sourceDir, string destDir) {
        Directory.CreateDirectory(destDir);
        
        foreach (var file in Directory.GetFiles(sourceDir)) {
            var destFile = Path.Combine(destDir, Path.GetFileName(file));
            File.Copy(file, destFile);
        }
        
        foreach (var dir in Directory.GetDirectories(sourceDir)) {
            var destSubDir = Path.Combine(destDir, Path.GetFileName(dir));
            CopyDirectory(dir, destSubDir);
        }
    }
    
    #endregion
}
