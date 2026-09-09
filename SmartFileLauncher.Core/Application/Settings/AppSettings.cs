namespace SmartFileLauncher.Core.Application.Settings;

public sealed class AppSettings
{
    public uint HotkeyModifiers { get; set; } = 2;
    public uint HotkeyKey { get; set; } = 0x20;
    public bool StartMinimized { get; set; }
    public bool StartWithWindows { get; set; }
    public bool MinimizeToTrayOnClose { get; set; } = true;
    public bool NaturalLanguageModeEnabled { get; set; }
    public bool GridViewEnabled { get; set; }
    public int SearchDebounceMs { get; set; } = 1200;
    public bool DiagnosticsLoggingEnabled { get; set; }
    public bool DiagnosticsMetricLoggingEnabled { get; set; }
    public int DiagnosticsMetricIntervalSeconds { get; set; } = 5;
    public string DiagnosticsLogDirectory { get; set; } = string.Empty;
    public bool RememberDiagnosticsLogDirectory { get; set; } = true;
    public bool CompactSearchStateEnabled { get; set; }
    public bool ThumbnailPreviewsEnabled { get; set; } = true;
    public bool ThumbnailCacheUseRamRatio { get; set; }
    public int ThumbnailCacheMaxCount { get; set; } = 1000;
    public double ThumbnailCacheRamPercent { get; set; } = 0.1;
    public int ThumbnailIdleSeconds { get; set; } = 60;
    public List<string> ThumbnailPinnedFolders { get; set; } = new();
    public bool HideThumbnailPinWarning { get; set; }

    public void ResetToDefaults()
    {
        HotkeyModifiers = 1;
        HotkeyKey = 0x20;
        StartMinimized = false;
        StartWithWindows = false;
        MinimizeToTrayOnClose = true;
        NaturalLanguageModeEnabled = false;
        GridViewEnabled = false;
        SearchDebounceMs = 1200;
        DiagnosticsLoggingEnabled = false;
        DiagnosticsMetricLoggingEnabled = false;
        DiagnosticsMetricIntervalSeconds = 5;
        DiagnosticsLogDirectory = string.Empty;
        RememberDiagnosticsLogDirectory = true;
        CompactSearchStateEnabled = false;
        ThumbnailPreviewsEnabled = true;
        ThumbnailCacheUseRamRatio = false;
        ThumbnailCacheMaxCount = 1000;
        ThumbnailCacheRamPercent = 0.1;
        ThumbnailIdleSeconds = 60;
        ThumbnailPinnedFolders = new();
        HideThumbnailPinWarning = false;
    }
}
