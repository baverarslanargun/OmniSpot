using System.Windows;
using System.Windows.Automation.Peers;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using System.Windows.Threading;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private const string UndoShortcut = "Ctrl+Z";
    private static readonly TimeSpan ToastDuration = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan ActionableToastDuration = TimeSpan.FromSeconds(7);
    private readonly DispatcherTimer _toastTimer = new() { Interval = ToastDuration };
    private Action? _toastAction;
    private string? _toastShortcut;

    private void InitializeToast()
    {
        ToastPopup.CustomPopupPlacementCallback = PlaceToast;
        Activated += (_, _) => SyncToastPopup();
        Deactivated += (_, _) => SyncToastPopup();
        StateChanged += (_, _) => SyncToastPopup();
        IsVisibleChanged += (_, _) => SyncToastPopup();
        LocationChanged += (_, _) => RepositionToast();
        Shell.SizeChanged += (_, _) => RepositionToast();
        _toastTimer.Tick += (_, _) => HideToast();
        Toast.MouseEnter += (_, _) => _toastTimer.Stop();
        Toast.MouseLeave += (_, _) =>
        {
            if (Toast.Visibility == Visibility.Visible) _toastTimer.Start();
        };
        ToastClose.Click += (_, _) => HideToast();
        ToastAction.Click += (_, _) => RunToastAction();
        PreviewKeyDown += (_, e) =>
        {
            if (e.Key != Key.Z || Keyboard.Modifiers != ModifierKeys.Control || NameEditing ||
                _toastShortcut != UndoShortcut || _toastAction == null || Toast.Visibility != Visibility.Visible)
                return;
            e.Handled = true;
            RunToastAction();
        };
    }

    private void ShowToast(string text, bool error = false, string? actionText = null, Action? action = null, string? shortcut = null)
    {
        if (_isPreparedForShutdown) return;
        ToastText.Text = text;
        ToastErrorIcon.Visibility = error ? Visibility.Visible : Visibility.Collapsed;
        var actionable = action != null && actionText != null;
        _toastAction = actionable ? action : null;
        _toastShortcut = actionable ? shortcut : null;
        ToastAction.Content = actionText;
        ToastAction.Visibility = actionable ? Visibility.Visible : Visibility.Collapsed;
        ToastShortcutText.Text = _toastShortcut;
        ToastShortcut.Visibility = _toastShortcut != null ? Visibility.Visible : Visibility.Collapsed;

        var appearing = Toast.Visibility != Visibility.Visible;
        Toast.Visibility = Visibility.Visible;
        SyncToastPopup();
        if (appearing) AnimatePanel(Toast, 0, 8, 0);

        _toastTimer.Stop();
        _toastTimer.Interval = error || actionable ? ActionableToastDuration : ToastDuration;
        if (!Toast.IsMouseOver) _toastTimer.Start();

        if (AutomationPeer.ListenerExists(AutomationEvents.LiveRegionChanged))
            (UIElementAutomationPeer.FromElement(Toast) ?? UIElementAutomationPeer.CreatePeerForElement(Toast))
                ?.RaiseAutomationEvent(AutomationEvents.LiveRegionChanged);
    }

    private void ShowErrorToast(string text, string? actionText = null, Action? action = null) =>
        ShowToast(text, error: true, actionText, action);

    private void RunToastAction()
    {
        var action = _toastAction;
        HideToast();
        action?.Invoke();
    }

    private void HideToast()
    {
        _toastTimer.Stop();
        _toastAction = null;
        _toastShortcut = null;
        Toast.Visibility = Visibility.Collapsed;
        SyncToastPopup();
    }

    private static CustomPopupPlacement[] PlaceToast(Size popup, Size target, Point offset) =>
    [
        new(new Point((target.Width - popup.Width) / 2, target.Height), PopupPrimaryAxis.Horizontal),
        new(new Point((target.Width - popup.Width) / 2, -popup.Height), PopupPrimaryAxis.Horizontal)
    ];

    private void SyncToastPopup()
    {
        var open = Toast.Visibility == Visibility.Visible && IsVisible && IsActive &&
            WindowState != WindowState.Minimized && !_isPreparedForShutdown;
        if (ToastPopup.IsOpen != open) ToastPopup.IsOpen = open;
    }

    private void RepositionToast()
    {
        if (!ToastPopup.IsOpen) return;
        var offset = ToastPopup.HorizontalOffset;
        ToastPopup.HorizontalOffset = offset + 1;
        ToastPopup.HorizontalOffset = offset;
    }
}
