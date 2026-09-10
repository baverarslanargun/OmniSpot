using System.Windows;
using System.Windows.Input;

namespace SmartFileLauncher.UI.Views;

public partial class ThumbnailPinConfirmationWindow : Window
{
    public ThumbnailPinConfirmationWindow(string folder)
    {
        InitializeComponent();
        FolderText.Text = folder;
        SourceInitialized += (_, _) => ModernDialog.ApplyRoundedCorners(this);
    }

    public bool HideFutureWarnings => DoNotShowAgain.IsChecked == true;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;

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
            e.OriginalSource is not System.Windows.Controls.CheckBox)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }
}
