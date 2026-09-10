using System.Runtime.InteropServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;

namespace SmartFileLauncher.UI.Views;

public enum DialogKind
{
    Info,
    Question,
    Warning,
    Danger
}

public partial class ModernDialog : Window
{
    public ModernDialog(string title, string message, DialogKind kind, string primaryText, string? secondaryText)
    {
        InitializeComponent();
        TitleText.Text = title;
        Title = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrWhiteSpace(message) ? Visibility.Collapsed : Visibility.Visible;
        PrimaryButton.Content = primaryText;
        if (secondaryText is null)
        {
            SecondaryButton.Visibility = Visibility.Collapsed;
        }
        else
        {
            SecondaryButton.Content = secondaryText;
        }

        ApplyKind(kind);
        SourceInitialized += (_, _) => ApplyRoundedCorners(this);
        Loaded += (_, _) => PrimaryButton.Focus();
    }

    public UIElement? Extra
    {
        get => ExtraContent.Content as UIElement;
        set
        {
            ExtraContent.Content = value;
            ExtraContent.Visibility = value is null ? Visibility.Collapsed : Visibility.Visible;
        }
    }

    public static void Show(Window? owner, string title, string message, DialogKind kind = DialogKind.Info, string buttonText = "Tamam")
    {
        var dialog = new ModernDialog(title, message, kind, buttonText, null);
        AttachOwner(dialog, owner);
        dialog.ShowDialog();
    }

    public static bool Confirm(Window? owner, string title, string message, string confirmText, DialogKind kind = DialogKind.Question, string cancelText = "Vazgeç")
    {
        var dialog = new ModernDialog(title, message, kind, confirmText, cancelText);
        AttachOwner(dialog, owner);
        return dialog.ShowDialog() == true;
    }

    internal static void AttachOwner(Window dialog, Window? owner)
    {
        owner ??= System.Windows.Application.Current?.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive)
                  ?? System.Windows.Application.Current?.MainWindow;
        if (owner is { IsVisible: true } && !ReferenceEquals(owner, dialog))
        {
            dialog.Owner = owner;
            dialog.WindowStartupLocation = WindowStartupLocation.CenterOwner;
        }
        else
        {
            dialog.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        }
    }

    private void ApplyKind(DialogKind kind)
    {
        var (tile, ink, geometry, danger) = kind switch
        {
            DialogKind.Question => ("#EEF4FF", "#3F7EE8", "DialogQuestionGeometry", false),
            DialogKind.Warning => ("#FFF3DD", "#D98A0C", "DialogWarningGeometry", false),
            DialogKind.Danger => ("#FDE8E6", "#DC4B3C", "DialogDangerGeometry", true),
            _ => ("#EEF4FF", "#3F7EE8", "DialogInfoGeometry", false)
        };
        IconTile.Background = Brush(tile);
        IconPath.Stroke = Brush(ink);
        IconPath.Data = (Geometry)FindResource(geometry);
        if (danger)
        {
            PrimaryButton.Style = (Style)FindResource("DialogDangerButton");
        }
    }

    private static SolidColorBrush Brush(string hex)
    {
        var brush = new SolidColorBrush((System.Windows.Media.Color)System.Windows.Media.ColorConverter.ConvertFromString(hex));
        brush.Freeze();
        return brush;
    }

    private void Primary_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = true;
    }

    private void Secondary_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase &&
            e.OriginalSource is not System.Windows.Controls.TextBox)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr hwnd, int attribute, ref int value, int size);

    internal static void ApplyRoundedCorners(Window window)
    {
        if (Environment.OSVersion.Version.Build < 22000) return;
        var hwnd = new WindowInteropHelper(window).Handle;
        if (hwnd == IntPtr.Zero) return;
        var preference = 2;
        DwmSetWindowAttribute(hwnd, 33, ref preference, sizeof(int));
    }
}
