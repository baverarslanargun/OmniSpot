using System.IO;
using System.Windows;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.UI.Services;

public sealed class ApplicationShellService : IApplicationShellService
{
    private readonly GlobalHotkeyService _hotkey;
    private readonly Action<string> _log;
    private System.Windows.Forms.NotifyIcon? _notifyIcon;
    private System.Windows.Forms.ContextMenuStrip? _contextMenu;
    private System.Drawing.Icon? _ownedIcon;
    private bool _initialized;
    private bool _disposed;

    public ApplicationShellService(
        GlobalHotkeyService hotkey,
        Action<string> log)
    {
        _hotkey = hotkey ?? throw new ArgumentNullException(nameof(hotkey));
        _log = log ?? throw new ArgumentNullException(nameof(log));
    }

    public event Action? ToggleRequested;
    public event Action? ShowRequested;
    public event Action? SettingsRequested;
    public event Action? ExitRequested;

    public void Initialize(Window window, AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(window);
        ArgumentNullException.ThrowIfNull(settings);
        if (_initialized)
        {
            return;
        }

        _initialized = true;
        _hotkey.HotkeyPressed += HandleHotkeyPressed;
        _hotkey.Initialize(window);
        ApplyHotkey(settings);
        SetupTray();
    }

    public void SuspendHotkey()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        _hotkey.UnregisterHotkey();
    }

    public void ApplyHotkey(AppSettings settings)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        ArgumentNullException.ThrowIfNull(settings);
        var modifiers =
            (GlobalHotkeyService.ModifierKeys)settings.HotkeyModifiers;
        var registered = _hotkey.RegisterHotkey(
            modifiers,
            settings.HotkeyKey);
        _log(registered
            ? $"✅ Global kısayol kaydedildi: {_hotkey.GetHotkeyString()}"
            : $"⚠️ Global kısayol kaydedilemedi: {_hotkey.GetHotkeyString()}");
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _hotkey.HotkeyPressed -= HandleHotkeyPressed;
        _hotkey.Dispose();

        if (_notifyIcon != null)
        {
            _notifyIcon.Visible = false;
            _notifyIcon.Dispose();
            _notifyIcon = null;
        }

        _ownedIcon?.Dispose();
        _ownedIcon = null;

        _contextMenu?.Dispose();
        _contextMenu = null;
    }

    private static System.Drawing.Icon? LoadTrayIcon()
    {
        var size = System.Windows.Forms.SystemInformation.SmallIconSize;
        var resource = System.Windows.Application.GetResourceStream(
            new Uri("pack://application:,,,/Resources/app.ico", UriKind.Absolute));
        if (resource != null)
        {
            using var stream = resource.Stream;
            return new System.Drawing.Icon(stream, size);
        }

        var iconPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "omnispot.ico");
        return File.Exists(iconPath) ? new System.Drawing.Icon(iconPath, size) : null;
    }

    private void SetupTray()
    {
        _notifyIcon = new System.Windows.Forms.NotifyIcon
        {
            Text = "OmniSpot"
        };

        try
        {
            _ownedIcon = LoadTrayIcon();
            _notifyIcon.Icon = _ownedIcon ?? System.Drawing.SystemIcons.Application;
        }
        catch
        {
            _ownedIcon?.Dispose();
            _ownedIcon = null;
            _notifyIcon.Icon = System.Drawing.SystemIcons.Application;
        }

        _notifyIcon.DoubleClick += (_, _) => ShowRequested?.Invoke();
        _contextMenu = new System.Windows.Forms.ContextMenuStrip();
        AddMenuItem("Göster", () => ShowRequested?.Invoke());
        AddMenuItem("Ayarlar", () => SettingsRequested?.Invoke());
        _contextMenu.Items.Add(
            new System.Windows.Forms.ToolStripSeparator());
        AddMenuItem("Çıkış", () => ExitRequested?.Invoke());
        _notifyIcon.ContextMenuStrip = _contextMenu;
        _notifyIcon.Visible = true;
        _log("📌 System tray ikonu hazır");
    }

    private void AddMenuItem(string text, Action action)
    {
        var item = new System.Windows.Forms.ToolStripMenuItem(text);
        item.Click += (_, _) => action();
        _contextMenu!.Items.Add(item);
    }

    private void HandleHotkeyPressed(object? sender, EventArgs e)
    {
        ToggleRequested?.Invoke();
    }
}
