using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFileLauncher.UI.Models;

/// <summary>
/// Uygulama ayarlarını temsil eden model
/// </summary>
public class AppSettings
{
    /// <summary>
    /// Ayarlar dosyasının yolu
    /// </summary>
    private static readonly string SettingsPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OmniSpot",
        "settings.json"
    );

    /// <summary>
    /// Global hotkey için modifier tuşları (Alt, Ctrl, Shift, Win)
    /// 1=Alt, 2=Ctrl, 4=Shift, 8=Win
    /// </summary>
    public uint HotkeyModifiers { get; set; } = 2; // Ctrl (değiştirildi: Alt -> Ctrl, Windows Alt+Space çakışmasını önlemek için)

    /// <summary>
    /// Global hotkey için tuş kodu
    /// </summary>
    public uint HotkeyKey { get; set; } = 0x20; // Space

    /// <summary>
    /// Uygulama başlangıçta minimize başlasın mı?
    /// </summary>
    public bool StartMinimized { get; set; } = false;

    /// <summary>
    /// Windows ile birlikte başlasın mı?
    /// </summary>
    public bool StartWithWindows { get; set; } = false;

    /// <summary>
    /// Kapatma butonuna basıldığında minimize olsun mu?
    /// </summary>
    public bool MinimizeToTrayOnClose { get; set; } = true;

    /// <summary>
    /// Doğal dil modu varsayılan olarak açık mı?
    /// </summary>
    public bool NaturalLanguageModeEnabled { get; set; } = false;

    /// <summary>
    /// Grid görünümü varsayılan olarak açık mı?
    /// </summary>
    public bool GridViewEnabled { get; set; } = false;

    /// <summary>
    /// Arama debounce gecikmesi (ms)
    /// </summary>
    public int SearchDebounceMs { get; set; } = 1200;

    /// <summary>
    /// Cache (SQLite) kullanılsın mı?
    /// </summary>
    public bool UseCachedIndex { get; set; } = true;

    /// <summary>
    /// Cache dosyasının yolu (salt okunur)
    /// </summary>
    [JsonIgnore]
    public static string CachePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
        "OmniSpot",
        "index.db"
    );

    /// <summary>
    /// Cache dosyası var mı?
    /// </summary>
    [JsonIgnore]
    public bool CacheExists => File.Exists(CachePath);

    /// <summary>
    /// Cache dosya boyutu (KB)
    /// </summary>
    [JsonIgnore]
    public long CacheSizeKB
    {
        get
        {
            try
            {
                if (CacheExists)
                {
                    var fi = new FileInfo(CachePath);
                    return fi.Length / 1024;
                }
            }
            catch { }
            return 0;
        }
    }

    /// <summary>
    /// Hotkey string formatında (görüntüleme için)
    /// </summary>
    [JsonIgnore]
    public string HotkeyDisplayString
    {
        get
        {
            var parts = new List<string>();
            var modifiers = (Services.GlobalHotkeyService.ModifierKeys)HotkeyModifiers;
            
            if (modifiers.HasFlag(Services.GlobalHotkeyService.ModifierKeys.Control)) parts.Add("Ctrl");
            if (modifiers.HasFlag(Services.GlobalHotkeyService.ModifierKeys.Alt)) parts.Add("Alt");
            if (modifiers.HasFlag(Services.GlobalHotkeyService.ModifierKeys.Shift)) parts.Add("Shift");
            if (modifiers.HasFlag(Services.GlobalHotkeyService.ModifierKeys.Win)) parts.Add("Win");
            
            parts.Add(Services.GlobalHotkeyService.KeyToString(HotkeyKey));
            return string.Join(" + ", parts);
        }
    }

    /// <summary>
    /// Ayarları dosyadan yükler. Dosya yoksa varsayılan ayarlar döner.
    /// </summary>
    public static AppSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<AppSettings>(json);
                return settings ?? new AppSettings();
            }
        }
        catch
        {
            // Hata durumunda varsayılan ayarları kullan
        }
        
        return new AppSettings();
    }

    /// <summary>
    /// Ayarları dosyaya kaydeder
    /// </summary>
    public void Save()
    {
        try
        {
            var directory = Path.GetDirectoryName(SettingsPath);
            if (!string.IsNullOrEmpty(directory) && !Directory.Exists(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var options = new JsonSerializerOptions
            {
                WriteIndented = true
            };
            var json = JsonSerializer.Serialize(this, options);
            File.WriteAllText(SettingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"Ayarlar kaydedilemedi: {ex.Message}");
        }
    }

    /// <summary>
    /// Varsayılan ayarlara sıfırlar
    /// </summary>
    public void ResetToDefaults()
    {
        HotkeyModifiers = 1; // Alt
        HotkeyKey = 0x20; // Space
        StartMinimized = false;
        StartWithWindows = false;
        MinimizeToTrayOnClose = true;
        NaturalLanguageModeEnabled = false;
        GridViewEnabled = false;
        SearchDebounceMs = 1200;
        UseCachedIndex = true;
    }

    /// <summary>
    /// Cache dosyasını siler
    /// </summary>
    public static bool DeleteCache()
    {
        try
        {
            if (File.Exists(CachePath))
            {
                File.Delete(CachePath);
                // WAL ve SHM dosyalarını da sil
                var walPath = CachePath + "-wal";
                var shmPath = CachePath + "-shm";
                if (File.Exists(walPath)) File.Delete(walPath);
                if (File.Exists(shmPath)) File.Delete(shmPath);
                return true;
            }
        }
        catch { }
        return false;
    }
}
