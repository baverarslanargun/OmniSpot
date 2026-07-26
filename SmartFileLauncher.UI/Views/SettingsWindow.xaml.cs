using System;
using System.Windows;
using System.Windows.Input;
using SmartFileLauncher.UI.Models;
using SmartFileLauncher.UI.Services;

namespace SmartFileLauncher.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly Action<string>? _log;
    
    private bool _isRecordingHotkey = false;
    private GlobalHotkeyService.ModifierKeys _pendingModifiers;
    private uint _pendingKey;
    
    /// <summary>
    /// Ayarlar değiştiğinde tetiklenir
    /// </summary>
    public event EventHandler<AppSettings>? SettingsChanged;

    public SettingsWindow(AppSettings settings, Action<string>? log = null)
    {
        InitializeComponent();
        _settings = settings;
        _log = log;
        
        LoadSettingsToUI();
        
        // Keyboard event'lerini yakala
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    /// <summary>
    /// Ayarları UI elementlerine yükler
    /// </summary>
    private void LoadSettingsToUI()
    {
        // Hotkey
        _pendingModifiers = (GlobalHotkeyService.ModifierKeys)_settings.HotkeyModifiers;
        _pendingKey = _settings.HotkeyKey;
        UpdateHotkeyDisplay();
        
        // Startup
        StartMinimizedCheckbox.IsChecked = _settings.StartMinimized;
        StartWithWindowsCheckbox.IsChecked = _settings.StartWithWindows;
        MinimizeToTrayCheckbox.IsChecked = _settings.MinimizeToTrayOnClose;
        
        // Defaults
        NaturalLanguageDefaultCheckbox.IsChecked = _settings.NaturalLanguageModeEnabled;
        GridViewDefaultCheckbox.IsChecked = _settings.GridViewEnabled;
        
        // Cache
        UseCacheCheckbox.IsChecked = _settings.UseCachedIndex;
        UpdateCacheStatus();
    }

    /// <summary>
    /// Cache durumunu günceller
    /// </summary>
    private void UpdateCacheStatus()
    {
        if (_settings.CacheExists)
        {
            CacheStatusText.Text = "✅ Önbellek mevcut";
            CacheStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x1F));
            CacheSizeText.Text = $"Boyut: {_settings.CacheSizeKB:N0} KB";
            ClearCacheButton.IsEnabled = true;
        }
        else
        {
            CacheStatusText.Text = "❌ Önbellek bulunamadı";
            CacheStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x86, 0x86, 0x8B));
            CacheSizeText.Text = "Boyut: -";
            ClearCacheButton.IsEnabled = false;
        }
        
        CachePathText.Text = $"Konum: {AppSettings.CachePath}";
    }

    /// <summary>
    /// Cache'i temizler
    /// </summary>
    private void ClearCache_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Önbellek temizlenecek. Sonraki açılışta dosyalar yeniden taranacak.\n\nDevam etmek istiyor musunuz?",
            "Önbelleği Temizle",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            // Uygulamayı yeniden başlatmak gerektiğini bildir
            var restartResult = System.Windows.MessageBox.Show(
                "Önbelleği temizlemek için uygulama yeniden başlatılmalı.\n\n" +
                "Şimdi yeniden başlatmak ister misiniz?",
                "Yeniden Başlatma Gerekli",
                MessageBoxButton.YesNo,
                MessageBoxImage.Information);
            
            if (restartResult == MessageBoxResult.Yes)
            {
                // Cache dosyasını sil ve uygulamayı yeniden başlat
                try
                {
                    var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                    if (!string.IsNullOrEmpty(exePath))
                    {
                        // Yeniden başlatma için batch dosyası oluştur
                        var batchPath = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "omnispot_restart.bat");
                        var commands = $@"
@echo off
timeout /t 2 /nobreak > nul
del ""{AppSettings.CachePath}"" 2>nul
del ""{AppSettings.CachePath}-wal"" 2>nul  
del ""{AppSettings.CachePath}-shm"" 2>nul
start """" ""{exePath}""
del ""%~f0""
";
                        System.IO.File.WriteAllText(batchPath, commands);
                        
                        var startInfo = new System.Diagnostics.ProcessStartInfo
                        {
                            FileName = batchPath,
                            CreateNoWindow = true,
                            UseShellExecute = true,
                            WindowStyle = System.Diagnostics.ProcessWindowStyle.Hidden
                        };
                        System.Diagnostics.Process.Start(startInfo);
                        
                        _log?.Invoke("🔄 Uygulama yeniden başlatılıyor...");
                        
                        // Önce bu pencereyi kapat, sonra uygulamayı kapat
                        this.Close();
                        
                        // Dispatcher üzerinden shutdown yap (daha güvenli)
                        Dispatcher.BeginInvoke(new Action(() => 
                        {
                            Environment.Exit(0);
                        }), System.Windows.Threading.DispatcherPriority.Background);
                        
                        return;
                    }
                }
                catch (Exception ex)
                {
                    System.Windows.MessageBox.Show(
                        $"Yeniden başlatma hatası: {ex.Message}\n\nLütfen uygulamayı manuel olarak yeniden başlatın.",
                        "Hata",
                        MessageBoxButton.OK,
                        MessageBoxImage.Error);
                }
            }
        }
    }

    /// <summary>
    /// Cache klasörünü açar
    /// </summary>
    private void OpenCacheFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            var folderPath = System.IO.Path.GetDirectoryName(AppSettings.CachePath);
            if (!string.IsNullOrEmpty(folderPath) && System.IO.Directory.Exists(folderPath))
            {
                System.Diagnostics.Process.Start("explorer.exe", folderPath);
            }
            else
            {
                System.Windows.MessageBox.Show("Önbellek klasörü bulunamadı.", "Uyarı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"⚠️ Klasör açılamadı: {ex.Message}");
        }
    }

    /// <summary>
    /// Hotkey gösterimini günceller
    /// </summary>
    private void UpdateHotkeyDisplay()
    {
        var modStr = GlobalHotkeyService.ModifiersToString(_pendingModifiers);
        var keyStr = GlobalHotkeyService.KeyToString(_pendingKey);
        CurrentHotkeyText.Text = string.IsNullOrEmpty(modStr) ? keyStr : $"{modStr} + {keyStr}";
    }

    /// <summary>
    /// Kısayol değiştirme modunu başlatır
    /// </summary>
    private void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = true;
        HotkeyRecordingPanel.Visibility = Visibility.Visible;
        ChangeHotkeyButton.IsEnabled = false;
        RecordingHotkeyText.Text = "Bir tuş kombinasyonu basın (örn: Alt+Space, Ctrl+Shift+O)";
        
        _log?.Invoke("🎹 Kısayol kayıt modu aktif");
    }

    /// <summary>
    /// Klavye girişlerini yakalar (hotkey kayıt modu için)
    /// </summary>
    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isRecordingHotkey) return;
        
        // Sadece modifier tuşlarsa bekle
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LWin || e.Key == Key.RWin)
        {
            // Modifier'ları güncelle ama tuş bekle
            UpdatePendingModifiers();
            RecordingHotkeyText.Text = $"{GlobalHotkeyService.ModifiersToString(_pendingModifiers)} + ...";
            e.Handled = true;
            return;
        }
        
        // Escape kayıtı iptal eder
        if (e.Key == Key.Escape)
        {
            CancelHotkeyRecording();
            e.Handled = true;
            return;
        }
        
        // Modifier'ları kontrol et
        UpdatePendingModifiers();
        
        // En az bir modifier gerekli
        if (_pendingModifiers == GlobalHotkeyService.ModifierKeys.None)
        {
            RecordingHotkeyText.Text = "⚠️ En az bir modifier tuşu gerekli (Ctrl, Alt, Shift veya Win)";
            e.Handled = true;
            return;
        }
        
        // Tuşu al
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _pendingKey = GlobalHotkeyService.KeyToVirtualKeyCode(key);
        
        // Gösterimi güncelle
        var modStr = GlobalHotkeyService.ModifiersToString(_pendingModifiers);
        var keyStr = GlobalHotkeyService.KeyToString(_pendingKey);
        RecordingHotkeyText.Text = $"✓ {modStr} + {keyStr}";
        
        _log?.Invoke($"🎹 Kısayol seçildi: {modStr} + {keyStr}");
        
        e.Handled = true;
    }

    /// <summary>
    /// Mevcut modifier tuşlarını kontrol eder
    /// </summary>
    private void UpdatePendingModifiers()
    {
        _pendingModifiers = GlobalHotkeyService.ModifierKeys.None;
        
        if (Keyboard.IsKeyDown(Key.LeftCtrl) || Keyboard.IsKeyDown(Key.RightCtrl))
            _pendingModifiers |= GlobalHotkeyService.ModifierKeys.Control;
        
        if (Keyboard.IsKeyDown(Key.LeftAlt) || Keyboard.IsKeyDown(Key.RightAlt))
            _pendingModifiers |= GlobalHotkeyService.ModifierKeys.Alt;
        
        if (Keyboard.IsKeyDown(Key.LeftShift) || Keyboard.IsKeyDown(Key.RightShift))
            _pendingModifiers |= GlobalHotkeyService.ModifierKeys.Shift;
        
        if (Keyboard.IsKeyDown(Key.LWin) || Keyboard.IsKeyDown(Key.RWin))
            _pendingModifiers |= GlobalHotkeyService.ModifierKeys.Win;
    }

    /// <summary>
    /// Kısayolu onaylar
    /// </summary>
    private void ConfirmHotkey_Click(object sender, RoutedEventArgs e)
    {
        if (_pendingModifiers == GlobalHotkeyService.ModifierKeys.None)
        {
            System.Windows.MessageBox.Show("Lütfen geçerli bir kısayol kombinasyonu girin.", "Uyarı", 
                MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        _isRecordingHotkey = false;
        HotkeyRecordingPanel.Visibility = Visibility.Collapsed;
        ChangeHotkeyButton.IsEnabled = true;
        
        UpdateHotkeyDisplay();
        _log?.Invoke($"✓ Kısayol güncellendi: {CurrentHotkeyText.Text}");
    }

    /// <summary>
    /// Kısayol kayıt modunu iptal eder
    /// </summary>
    private void CancelHotkey_Click(object sender, RoutedEventArgs e)
    {
        CancelHotkeyRecording();
    }

    private void CancelHotkeyRecording()
    {
        _isRecordingHotkey = false;
        HotkeyRecordingPanel.Visibility = Visibility.Collapsed;
        ChangeHotkeyButton.IsEnabled = true;
        
        // Eski değerleri geri yükle
        _pendingModifiers = (GlobalHotkeyService.ModifierKeys)_settings.HotkeyModifiers;
        _pendingKey = _settings.HotkeyKey;
        
        _log?.Invoke("🚫 Kısayol değişikliği iptal edildi");
    }

    /// <summary>
    /// Varsayılan ayarlara sıfırlar
    /// </summary>
    private void ResetDefaults_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Tüm ayarlar varsayılan değerlere sıfırlanacak. Devam etmek istiyor musunuz?",
            "Varsayılanlara Sıfırla",
            MessageBoxButton.YesNo,
            MessageBoxImage.Question);
        
        if (result == MessageBoxResult.Yes)
        {
            _settings.ResetToDefaults();
            LoadSettingsToUI();
            _log?.Invoke("🔄 Ayarlar varsayılanlara sıfırlandı");
        }
    }

    /// <summary>
    /// Ayarları kaydeder ve pencereyi kapatır
    /// </summary>
    private void Save_Click(object sender, RoutedEventArgs e)
    {
        // UI'dan ayarlara aktar
        _settings.HotkeyModifiers = (uint)_pendingModifiers;
        _settings.HotkeyKey = _pendingKey;
        _settings.StartMinimized = StartMinimizedCheckbox.IsChecked ?? false;
        _settings.StartWithWindows = StartWithWindowsCheckbox.IsChecked ?? false;
        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckbox.IsChecked ?? true;
        _settings.NaturalLanguageModeEnabled = NaturalLanguageDefaultCheckbox.IsChecked ?? false;
        _settings.GridViewEnabled = GridViewDefaultCheckbox.IsChecked ?? false;
        _settings.UseCachedIndex = UseCacheCheckbox.IsChecked ?? true;
        
        // Dosyaya kaydet
        _settings.Save();
        
        // Windows ile başlatma ayarını uygula
        ApplyStartWithWindows(_settings.StartWithWindows);
        
        _log?.Invoke("💾 Ayarlar kaydedildi");
        
        // Event tetikle
        SettingsChanged?.Invoke(this, _settings);
        
        DialogResult = true;
        Close();
    }

    /// <summary>
    /// Windows ile başlatma özelliğini ayarlar
    /// </summary>
    private void ApplyStartWithWindows(bool enable)
    {
        try
        {
            var key = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run", true);
            
            if (key == null) return;
            
            if (enable)
            {
                var exePath = System.Diagnostics.Process.GetCurrentProcess().MainModule?.FileName;
                if (!string.IsNullOrEmpty(exePath))
                {
                    key.SetValue("OmniSpot", $"\"{exePath}\"");
                }
            }
            else
            {
                key.DeleteValue("OmniSpot", false);
            }
            
            key.Close();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"⚠️ Registry ayarı yapılamadı: {ex.Message}");
        }
    }
}
