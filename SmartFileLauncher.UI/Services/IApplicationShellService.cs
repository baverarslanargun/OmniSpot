using System.Windows;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.UI.Services;

public interface IApplicationShellService : IDisposable
{
    event Action? ToggleRequested;
    event Action? ShowRequested;
    event Action? SettingsRequested;
    event Action? ExitRequested;
    void Initialize(Window window, AppSettings settings);
    void SuspendHotkey();
    void ApplyHotkey(AppSettings settings);
}
