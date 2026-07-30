using System.Text.Json;

namespace SmartFileLauncher.Core.Application.Settings;

public sealed class JsonSettingsStore : ISettingsStore
{
    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true
    };

    private readonly string _settingsPath;

    public JsonSettingsStore(string settingsPath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(settingsPath);
        _settingsPath = settingsPath;
    }

    public AppSettings Load()
    {
        try
        {
            if (!File.Exists(_settingsPath))
            {
                return new AppSettings();
            }

            var json = File.ReadAllText(_settingsPath);
            return JsonSerializer.Deserialize<AppSettings>(json)
                ?? new AppSettings();
        }
        catch
        {
            return new AppSettings();
        }
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        try
        {
            var directory = Path.GetDirectoryName(_settingsPath);
            if (!string.IsNullOrEmpty(directory))
            {
                Directory.CreateDirectory(directory);
            }

            var json = JsonSerializer.Serialize(settings, SerializerOptions);
            File.WriteAllText(_settingsPath, json);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine(
                $"Ayarlar kaydedilemedi: {ex.Message}");
        }
    }
}
