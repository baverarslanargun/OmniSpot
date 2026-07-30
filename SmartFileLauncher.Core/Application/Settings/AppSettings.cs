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
    }
}
