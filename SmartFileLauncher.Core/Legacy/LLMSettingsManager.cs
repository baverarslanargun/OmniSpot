using System;
using System.IO;
using System.Text.Json;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Services;

public static class LLMSettingsManager
{
    private static readonly string SettingsPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "llmsettings.json");

    public static LLMSettings Load()
    {
        try
        {
            if (File.Exists(SettingsPath))
            {
                var json = File.ReadAllText(SettingsPath);
                var settings = JsonSerializer.Deserialize<LLMSettings>(json);
                if (settings != null)
                    return settings;
            }
        }
        catch { }
        return new LLMSettings();
    }

    public static void Save(LLMSettings settings)
    {
        try
        {
            var json = JsonSerializer.Serialize(settings, new JsonSerializerOptions { WriteIndented = true });
            File.WriteAllText(SettingsPath, json);
        }
        catch { }
    }

    public static string GetSettingsFilePath() => SettingsPath;
}