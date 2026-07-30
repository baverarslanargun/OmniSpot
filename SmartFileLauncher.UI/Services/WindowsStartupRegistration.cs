using Microsoft.Win32;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.UI.Services;

public sealed class WindowsStartupRegistration : IStartupRegistration
{
    private readonly Action<string> _log;

    public WindowsStartupRegistration(Action<string> log)
    {
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public void Apply(bool enabled)
    {
        try
        {
            using var key = Registry.CurrentUser.OpenSubKey(
                @"SOFTWARE\Microsoft\Windows\CurrentVersion\Run",
                writable: true);
            if (key == null)
            {
                return;
            }

            if (!enabled)
            {
                key.DeleteValue("OmniSpot", throwOnMissingValue: false);
                return;
            }

            var executablePath =
                System.Diagnostics.Process.GetCurrentProcess()
                    .MainModule?
                    .FileName;
            if (!string.IsNullOrEmpty(executablePath))
            {
                key.SetValue("OmniSpot", $"\"{executablePath}\"");
            }
        }
        catch (Exception ex)
        {
            _log($"⚠️ Registry ayarı yapılamadı: {ex.Message}");
        }
    }
}
