using System.Windows;

namespace SmartFileLauncher.UI.Views;

public partial class ThumbnailPinConfirmationWindow : Window
{
    public ThumbnailPinConfirmationWindow(string folder)
    {
        InitializeComponent();
        FolderText.Text = folder;
    }

    public bool HideFutureWarnings => DoNotShowAgain.IsChecked == true;

    private void Confirm_Click(object sender, RoutedEventArgs e) => DialogResult = true;
}
