using System;
using System.Windows;
using System.Windows.Input;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.UI.Services;

namespace SmartFileLauncher.UI.Views;

public partial class SettingsWindow : Window
{
    private readonly AppSettings _settings;
    private readonly ISettingsApplicationService _settingsApplication;
    private readonly IIndexMaintenanceService _indexMaintenance;
    private readonly Action<string>? _log;
    
    private bool _isRecordingHotkey = false;
    private GlobalHotkeyService.ModifierKeys _pendingModifiers;
    private uint _pendingKey;
    
    public event EventHandler<AppSettings>? SettingsChanged;
    public event EventHandler? IndexRebuildRequested;

    public SettingsWindow(
        AppSettings settings,
        ISettingsApplicationService settingsApplication,
        IIndexMaintenanceService indexMaintenance,
        Action<string>? log = null)
    {
        InitializeComponent();
        _settings = settings ?? throw new ArgumentNullException(nameof(settings));
        _settingsApplication = settingsApplication
            ?? throw new ArgumentNullException(nameof(settingsApplication));
        _indexMaintenance = indexMaintenance
            ?? throw new ArgumentNullException(nameof(indexMaintenance));
        _log = log;
        
        LoadSettingsToUI();
        
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
    }

    private void LoadSettingsToUI()
    {
        _pendingModifiers = (GlobalHotkeyService.ModifierKeys)_settings.HotkeyModifiers;
        _pendingKey = _settings.HotkeyKey;
        UpdateHotkeyDisplay();
        
        StartMinimizedCheckbox.IsChecked = _settings.StartMinimized;
        StartWithWindowsCheckbox.IsChecked = _settings.StartWithWindows;
        MinimizeToTrayCheckbox.IsChecked = _settings.MinimizeToTrayOnClose;
        
        NaturalLanguageDefaultCheckbox.IsChecked = _settings.NaturalLanguageModeEnabled;
        GridViewDefaultCheckbox.IsChecked = _settings.GridViewEnabled;
        
        UpdateIndexStatus();
    }

    private void UpdateIndexStatus()
    {
        var status = _indexMaintenance.GetStatus();
        if (status.Exists)
        {
            IndexStatusText.Text = "✅ İndeks mevcut";
            IndexStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x1D, 0x1D, 0x1F));
            IndexSizeText.Text = $"Boyut: {status.SizeKilobytes:N0} KB";
            RebuildIndexButton.IsEnabled = true;
        }
        else
        {
            IndexStatusText.Text = "⏳ İndeks henüz oluşturulmadı";
            IndexStatusText.Foreground = new System.Windows.Media.SolidColorBrush(
                System.Windows.Media.Color.FromRgb(0x86, 0x86, 0x8B));
            IndexSizeText.Text = "Boyut: -";
            RebuildIndexButton.IsEnabled = false;
        }
        
        IndexPathText.Text = $"Konum: {status.Path}";
    }

    private void RebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        var result = System.Windows.MessageBox.Show(
            "Mevcut arama indeksi silinecek. OmniSpot yeniden başlatılacak ve standart klasörler tekrar taranacak.\n\nDevam etmek istiyor musunuz?",
            "İndeksi Yeniden Oluştur",
            MessageBoxButton.YesNo,
            MessageBoxImage.Warning);

        if (result != MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            _indexMaintenance.ScheduleRebuild();
            _log?.Invoke("🔄 İndeks yeniden oluşturuluyor...");
            Close();
            IndexRebuildRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            System.Windows.MessageBox.Show(
                $"İndeks yeniden oluşturulamadı: {ex.Message}\n\nLütfen uygulamayı manuel olarak yeniden başlatın.",
                "Hata",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

    private void OpenIndexFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_indexMaintenance.OpenIndexFolder())
            {
                System.Windows.MessageBox.Show("İndeks klasörü bulunamadı.", "Uyarı",
                    MessageBoxButton.OK, MessageBoxImage.Warning);
            }
        }
        catch (Exception ex)
        {
            _log?.Invoke($"⚠️ Klasör açılamadı: {ex.Message}");
        }
    }

    private void UpdateHotkeyDisplay()
    {
        var modStr = GlobalHotkeyService.ModifiersToString(_pendingModifiers);
        var keyStr = GlobalHotkeyService.KeyToString(_pendingKey);
        CurrentHotkeyText.Text = string.IsNullOrEmpty(modStr) ? keyStr : $"{modStr} + {keyStr}";
    }

    private void ChangeHotkeyButton_Click(object sender, RoutedEventArgs e)
    {
        _isRecordingHotkey = true;
        HotkeyRecordingPanel.Visibility = Visibility.Visible;
        ChangeHotkeyButton.IsEnabled = false;
        RecordingHotkeyText.Text = "Bir tuş kombinasyonu basın (örn: Alt+Space, Ctrl+Shift+O)";
        
        _log?.Invoke("🎹 Kısayol kayıt modu aktif");
    }

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isRecordingHotkey) return;
        
        if (e.Key == Key.LeftCtrl || e.Key == Key.RightCtrl ||
            e.Key == Key.LeftAlt || e.Key == Key.RightAlt ||
            e.Key == Key.LeftShift || e.Key == Key.RightShift ||
            e.Key == Key.LWin || e.Key == Key.RWin)
        {
            UpdatePendingModifiers();
            RecordingHotkeyText.Text = $"{GlobalHotkeyService.ModifiersToString(_pendingModifiers)} + ...";
            e.Handled = true;
            return;
        }
        
        if (e.Key == Key.Escape)
        {
            CancelHotkeyRecording();
            e.Handled = true;
            return;
        }
        
        UpdatePendingModifiers();
        
        if (_pendingModifiers == GlobalHotkeyService.ModifierKeys.None)
        {
            RecordingHotkeyText.Text = "⚠️ En az bir modifier tuşu gerekli (Ctrl, Alt, Shift veya Win)";
            e.Handled = true;
            return;
        }
        
        var key = e.Key == Key.System ? e.SystemKey : e.Key;
        _pendingKey = GlobalHotkeyService.KeyToVirtualKeyCode(key);
        
        var modStr = GlobalHotkeyService.ModifiersToString(_pendingModifiers);
        var keyStr = GlobalHotkeyService.KeyToString(_pendingKey);
        RecordingHotkeyText.Text = $"✓ {modStr} + {keyStr}";
        
        _log?.Invoke($"🎹 Kısayol seçildi: {modStr} + {keyStr}");
        
        e.Handled = true;
    }

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

    private void CancelHotkey_Click(object sender, RoutedEventArgs e)
    {
        CancelHotkeyRecording();
    }

    private void CancelHotkeyRecording()
    {
        _isRecordingHotkey = false;
        HotkeyRecordingPanel.Visibility = Visibility.Collapsed;
        ChangeHotkeyButton.IsEnabled = true;
        
        _pendingModifiers = (GlobalHotkeyService.ModifierKeys)_settings.HotkeyModifiers;
        _pendingKey = _settings.HotkeyKey;
        
        _log?.Invoke("🚫 Kısayol değişikliği iptal edildi");
    }

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

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _settings.HotkeyModifiers = (uint)_pendingModifiers;
        _settings.HotkeyKey = _pendingKey;
        _settings.StartMinimized = StartMinimizedCheckbox.IsChecked ?? false;
        _settings.StartWithWindows = StartWithWindowsCheckbox.IsChecked ?? false;
        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckbox.IsChecked ?? true;
        _settings.NaturalLanguageModeEnabled = NaturalLanguageDefaultCheckbox.IsChecked ?? false;
        _settings.GridViewEnabled = GridViewDefaultCheckbox.IsChecked ?? false;

        try
        {
            _settingsApplication.Save(_settings);
            _log?.Invoke("💾 Ayarlar kaydedildi");
            SettingsChanged?.Invoke(this, _settings);
            DialogResult = true;
            Close();
        }
        catch (Exception ex)
        {
            _log?.Invoke($"⚠️ Ayarlar kaydedilemedi: {ex.Message}");
            System.Windows.MessageBox.Show(
                $"Ayarlar kaydedilemedi: {ex.Message}",
                "Hata",
                MessageBoxButton.OK,
                MessageBoxImage.Error);
        }
    }

}
