using System;
using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;

namespace SmartFileLauncher.UI.Services;

/// <summary>
/// Windows API kullanarak global hotkey (sistem genelinde kısayol tuşu) yönetimi sağlar.
/// Uygulama arka planda olsa bile kısayol tuşlarını yakalar.
/// </summary>
public class GlobalHotkeyService : IDisposable
{
    // Windows API imports
    [DllImport("user32.dll")]
    private static extern bool RegisterHotKey(IntPtr hWnd, int id, uint fsModifiers, uint vk);

    [DllImport("user32.dll")]
    private static extern bool UnregisterHotKey(IntPtr hWnd, int id);

    // Modifier keys
    [Flags]
    public enum ModifierKeys : uint
    {
        None = 0,
        Alt = 1,
        Control = 2,
        Shift = 4,
        Win = 8
    }

    // Virtual key codes for common keys
    public static class VirtualKeyCodes
    {
        public const uint Space = 0x20;
        public const uint Enter = 0x0D;
        public const uint Tab = 0x09;
        public const uint Escape = 0x1B;
        public const uint A = 0x41;
        public const uint B = 0x42;
        public const uint C = 0x43;
        public const uint D = 0x44;
        public const uint E = 0x45;
        public const uint F = 0x46;
        public const uint G = 0x47;
        public const uint H = 0x48;
        public const uint I = 0x49;
        public const uint J = 0x4A;
        public const uint K = 0x4B;
        public const uint L = 0x4C;
        public const uint M = 0x4D;
        public const uint N = 0x4E;
        public const uint O = 0x4F;
        public const uint P = 0x50;
        public const uint Q = 0x51;
        public const uint R = 0x52;
        public const uint S = 0x53;
        public const uint T = 0x54;
        public const uint U = 0x55;
        public const uint V = 0x56;
        public const uint W = 0x57;
        public const uint X = 0x58;
        public const uint Y = 0x59;
        public const uint Z = 0x5A;
        public const uint F1 = 0x70;
        public const uint F2 = 0x71;
        public const uint F3 = 0x72;
        public const uint F4 = 0x73;
        public const uint F5 = 0x74;
        public const uint F6 = 0x75;
        public const uint F7 = 0x76;
        public const uint F8 = 0x77;
        public const uint F9 = 0x78;
        public const uint F10 = 0x79;
        public const uint F11 = 0x7A;
        public const uint F12 = 0x7B;
    }

    private const int WM_HOTKEY = 0x0312;
    private const int HOTKEY_ID = 9000;

    private IntPtr _windowHandle;
    private HwndSource? _source;
    private bool _isRegistered;
    private bool _isDisposed;

    private ModifierKeys _currentModifiers = ModifierKeys.Alt;
    private uint _currentKey = VirtualKeyCodes.Space;

    /// <summary>
    /// Hotkey tetiklendiğinde çağrılır
    /// </summary>
    public event EventHandler? HotkeyPressed;

    /// <summary>
    /// Mevcut modifier tuşları (Alt, Ctrl, Shift, Win)
    /// </summary>
    public ModifierKeys CurrentModifiers => _currentModifiers;

    /// <summary>
    /// Mevcut tuş kodu
    /// </summary>
    public uint CurrentKey => _currentKey;

    /// <summary>
    /// Hotkey kayıtlı mı?
    /// </summary>
    public bool IsRegistered => _isRegistered;

    /// <summary>
    /// Servisi bir WPF penceresi ile başlatır
    /// </summary>
    public void Initialize(Window window)
    {
        if (_isDisposed)
            throw new ObjectDisposedException(nameof(GlobalHotkeyService));

        var helper = new WindowInteropHelper(window);
        _windowHandle = helper.Handle;

        if (_windowHandle == IntPtr.Zero)
        {
            // Pencere henüz oluşturulmamış, SourceInitialized event'ini bekle
            window.SourceInitialized += (s, e) =>
            {
                _windowHandle = new WindowInteropHelper(window).Handle;
                SetupMessageHook();
            };
        }
        else
        {
            SetupMessageHook();
        }
    }

    private void SetupMessageHook()
    {
        _source = HwndSource.FromHwnd(_windowHandle);
        _source?.AddHook(HwndHook);
    }

    /// <summary>
    /// Global hotkey'i kaydeder
    /// </summary>
    public bool RegisterHotkey(ModifierKeys modifiers, uint key)
    {
        if (_windowHandle == IntPtr.Zero)
            return false;

        // Önce mevcut kaydı kaldır
        UnregisterHotkey();

        _currentModifiers = modifiers;
        _currentKey = key;

        _isRegistered = RegisterHotKey(_windowHandle, HOTKEY_ID, (uint)modifiers, key);
        return _isRegistered;
    }

    /// <summary>
    /// Mevcut ayarlarla hotkey'i yeniden kaydeder
    /// </summary>
    public bool RegisterHotkey()
    {
        return RegisterHotkey(_currentModifiers, _currentKey);
    }

    /// <summary>
    /// Global hotkey kaydını kaldırır
    /// </summary>
    public void UnregisterHotkey()
    {
        if (_windowHandle != IntPtr.Zero && _isRegistered)
        {
            UnregisterHotKey(_windowHandle, HOTKEY_ID);
            _isRegistered = false;
        }
    }

    /// <summary>
    /// Windows mesajlarını işler
    /// </summary>
    private IntPtr HwndHook(IntPtr hwnd, int msg, IntPtr wParam, IntPtr lParam, ref bool handled)
    {
        if (msg == WM_HOTKEY && wParam.ToInt32() == HOTKEY_ID)
        {
            HotkeyPressed?.Invoke(this, EventArgs.Empty);
            handled = true;
        }
        return IntPtr.Zero;
    }

    /// <summary>
    /// Modifier flags'i insan tarafından okunabilir stringe çevirir
    /// </summary>
    public static string ModifiersToString(ModifierKeys modifiers)
    {
        var parts = new List<string>();
        if (modifiers.HasFlag(ModifierKeys.Control)) parts.Add("Ctrl");
        if (modifiers.HasFlag(ModifierKeys.Alt)) parts.Add("Alt");
        if (modifiers.HasFlag(ModifierKeys.Shift)) parts.Add("Shift");
        if (modifiers.HasFlag(ModifierKeys.Win)) parts.Add("Win");
        return string.Join(" + ", parts);
    }

    /// <summary>
    /// Virtual key code'u insan tarafından okunabilir stringe çevirir
    /// </summary>
    public static string KeyToString(uint keyCode)
    {
        return keyCode switch
        {
            VirtualKeyCodes.Space => "Space",
            VirtualKeyCodes.Enter => "Enter",
            VirtualKeyCodes.Tab => "Tab",
            VirtualKeyCodes.Escape => "Escape",
            >= VirtualKeyCodes.A and <= VirtualKeyCodes.Z => ((char)keyCode).ToString(),
            >= VirtualKeyCodes.F1 and <= VirtualKeyCodes.F12 => $"F{keyCode - VirtualKeyCodes.F1 + 1}",
            _ => $"Key({keyCode})"
        };
    }

    /// <summary>
    /// Tam hotkey string'ini döndürür (örn: "Alt + Space")
    /// </summary>
    public string GetHotkeyString()
    {
        var modStr = ModifiersToString(_currentModifiers);
        var keyStr = KeyToString(_currentKey);
        return string.IsNullOrEmpty(modStr) ? keyStr : $"{modStr} + {keyStr}";
    }

    /// <summary>
    /// System.Windows.Input.Key'i virtual key code'a çevirir
    /// </summary>
    public static uint KeyToVirtualKeyCode(System.Windows.Input.Key key)
    {
        return (uint)KeyInterop.VirtualKeyFromKey(key);
    }

    /// <summary>
    /// Virtual key code'u System.Windows.Input.Key'e çevirir
    /// </summary>
    public static System.Windows.Input.Key VirtualKeyCodeToKey(uint vk)
    {
        return KeyInterop.KeyFromVirtualKey((int)vk);
    }

    public void Dispose()
    {
        if (_isDisposed) return;
        
        UnregisterHotkey();
        _source?.RemoveHook(HwndHook);
        _source = null;
        _isDisposed = true;
    }
}
