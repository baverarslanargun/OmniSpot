using System;
using System.Threading.Tasks;
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
    private bool _indexStatusStarted;
    private bool _indexStatusClosed;
    
    private bool _isRecordingHotkey = false;
    private GlobalHotkeyService.ModifierKeys _pendingModifiers;
    private uint _pendingKey;
    
    public event EventHandler<AppSettings>? SettingsChanged;
    public event EventHandler? IndexRebuildRequested;
    public event EventHandler? RestartRequested;

    public SettingsWindow(
        AppSettings settings,
        ISettingsApplicationService settingsApplication,
        IIndexMaintenanceService indexMaintenance,
        Action<string>? log = null,
        Func<long>? thumbnailCacheBytes = null)
    {
        InitializeComponent();
        ArgumentNullException.ThrowIfNull(settings);
        _settings = System.Text.Json.JsonSerializer.Deserialize<AppSettings>(
            System.Text.Json.JsonSerializer.Serialize(settings))!;
        _thumbnailCacheBytes = thumbnailCacheBytes;
        _settingsApplication = settingsApplication
            ?? throw new ArgumentNullException(nameof(settingsApplication));
        _indexMaintenance = indexMaintenance
            ?? throw new ArgumentNullException(nameof(indexMaintenance));
        _log = log;
        
        LoadSettingsToUI();
        InitializeAiCredentials();
        StartThumbnailPreview();
        MaxHeight = SystemParameters.WorkArea.Height;
        
        PreviewKeyDown += SettingsWindow_PreviewKeyDown;
        Loaded += LoadIndexStatus;
        Closed += (_, _) => _indexStatusClosed = true;
        SourceInitialized += (_, _) => ModernDialog.ApplyRoundedCorners(this);
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
        CompactCatalogCheckbox.IsChecked = _settings.CompactSearchStateEnabled;
        InitializeIndexingSettings();
        
        IndexStatusText.Text = "İndeks bilgisi yükleniyor…";
        IndexSizeText.Text = "Boyut hesaplanıyor…";
        IndexPathText.Text = "Konum yükleniyor…";
        RebuildIndexButton.IsEnabled = false;
        InitializeThumbnailSettings();
    }

    private async void LoadIndexStatus(object sender, RoutedEventArgs e)
    {
        if (_indexStatusStarted) return;
        _indexStatusStarted = true;
        try
        {
            var status = await Task.Run(_indexMaintenance.GetStatus);
            if (!_indexStatusClosed) UpdateIndexStatus(status);
        }
        catch (Exception failure)
        {
            if (_indexStatusClosed) return;
            IndexStatusText.Text = "İndeks bilgisi okunamadı";
            IndexSizeText.Text = "Boyut: -";
            IndexPathText.Text = "Konum: -";
            _log?.Invoke($"⚠️ İndeks bilgisi okunamadı: {failure.Message}");
        }
    }

    private void UpdateIndexStatus(IndexStorageStatus status)
    {
        if (status.Exists)
        {
            IndexStatusText.Text = "İndeks mevcut";
            IndexStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Ink");
            IndexSizeText.Text = status.SizeKilobytes >= 1024
                ? $"Boyut: {FormatThumbnailBytes(status.SizeKilobytes * 1024)}"
                : $"Boyut: {status.SizeKilobytes:N0} KB";
            RebuildIndexButton.IsEnabled = true;
        }
        else
        {
            IndexStatusText.Text = "İndeks henüz oluşturulmadı";
            IndexStatusText.Foreground = (System.Windows.Media.Brush)FindResource("Ink2");
            IndexSizeText.Text = "Boyut: -";
            RebuildIndexButton.IsEnabled = false;
        }
        
        IndexPathText.Text = $"Konum: {status.Path}";
    }

    private void RebuildIndex_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveIndexingSettings()) return;
        if (!ModernDialog.Confirm(this, "İndeks yeniden oluşturulsun mu?",
                "Seçtiğiniz yöntem ve klasörler kaydedilecek. Mevcut arama indeksi yeniden oluşturulacak; kişisel dosyalarınız değişmez.",
                "Yeniden oluştur", DialogKind.Warning))
        {
            return;
        }

        try
        {
            ScheduleIndexRebuild();
            _log?.Invoke("🔄 İndeks yeniden oluşturuluyor...");
            Close();
            IndexRebuildRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ModernDialog.Show(this, "İndeks yeniden oluşturulamadı",
                $"{ex.Message}\n\nLütfen uygulamayı elle yeniden başlatın.", DialogKind.Danger);
        }
    }

    private void RestartApplication_Click(object sender, RoutedEventArgs e)
    {
        var result = ModernDialog.Confirm(this, "OmniSpot yeniden başlatılsın mı?",
            "Uygulama kapanıp yeniden açılacak. İndeks silinmez, kaydedilmemiş ayar değişiklikleri uygulanmaz.",
            "Yeniden başlat", DialogKind.Question);

        if (!result)
        {
            return;
        }

        try
        {
            _indexMaintenance.ScheduleRestart();
            _log?.Invoke("🔁 OmniSpot yeniden başlatılıyor...");
            Close();
            RestartRequested?.Invoke(this, EventArgs.Empty);
        }
        catch (Exception ex)
        {
            ModernDialog.Show(this, "Yeniden başlatılamadı",
                $"{ex.Message}\n\nLütfen uygulamayı elle kapatıp açın.", DialogKind.Danger);
        }
    }

    private void OpenIndexFolder_Click(object sender, RoutedEventArgs e)
    {
        try
        {
            if (!_indexMaintenance.OpenIndexFolder())
            {
                ModernDialog.Show(this, "İndeks klasörü bulunamadı", "Klasör henüz oluşturulmamış veya taşınmış olabilir.", DialogKind.Warning);
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

    private void Header_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is System.Windows.Controls.Primitives.ButtonBase) return;
        try { DragMove(); } catch (InvalidOperationException) { }
    }

    private void CloseWindow_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void SettingsWindow_PreviewKeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (!_isRecordingHotkey)
        {
            if (e.Key == Key.Escape && TryCancelAiKeyEdit()) {
                e.Handled = true;
                return;
            }

            if (e.Key == Key.Escape && Keyboard.FocusedElement is not System.Windows.Controls.TextBox)
            {
                Close();
                e.Handled = true;
            }
            return;
        }
        
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
            RecordingHotkeyText.Text = "En az bir değiştirici tuş gerekli (Ctrl, Alt, Shift veya Win)";
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
            ModernDialog.Show(this, "Geçersiz kısayol", "En az bir değiştirici tuşla (Ctrl, Alt, Shift, Win) birlikte bir tuş seçin.", DialogKind.Warning);
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
        var result = ModernDialog.Confirm(this, "Ayarlar sıfırlansın mı?",
            "Tüm ayarlar varsayılan değerlere dönecek. Kaydet'e basana kadar kalıcı olmaz.",
            "Sıfırla", DialogKind.Question);
        
        if (result)
        {
            _settings.ResetToDefaults();
            LoadSettingsToUI();
            _log?.Invoke("🔄 Ayarlar varsayılanlara sıfırlandı");
        }
    }

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        if (!SaveIndexingSettings()) return;
        if (!SaveThumbnailSettings()) return;
        _settings.HotkeyModifiers = (uint)_pendingModifiers;
        _settings.HotkeyKey = _pendingKey;
        _settings.StartMinimized = StartMinimizedCheckbox.IsChecked ?? false;
        _settings.StartWithWindows = StartWithWindowsCheckbox.IsChecked ?? false;
        _settings.MinimizeToTrayOnClose = MinimizeToTrayCheckbox.IsChecked ?? true;
        _settings.NaturalLanguageModeEnabled = NaturalLanguageDefaultCheckbox.IsChecked ?? false;
        _settings.GridViewEnabled = GridViewDefaultCheckbox.IsChecked ?? false;
        _settings.CompactSearchStateEnabled = CompactCatalogCheckbox.IsChecked ?? true;

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
            ModernDialog.Show(this, "Ayarlar kaydedilemedi", ex.Message, DialogKind.Danger);
        }
    }

}
