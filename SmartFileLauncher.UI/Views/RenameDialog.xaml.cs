using System.IO;
using System.Windows;
using System.Windows.Input;

namespace SmartFileLauncher.UI.Views;

public partial class RenameDialog : Window
{
    public string NewName { get; private set; } = "";

    public RenameDialog(string currentName, bool isNew = false)
    {
        InitializeComponent();

        NameTextBox.Text = currentName;
        NewName = currentName;

        if (isNew)
        {
            Title = "Yeni oluştur";
            TitleText.Text = "Yeni oluştur";
            LabelText.Text = "Ad girin:";
            OkButton.Content = "Oluştur";
        }
        else
        {
            Title = "Yeniden adlandır";
            TitleText.Text = "Yeniden adlandır";
            LabelText.Text = "Yeni ad girin:";
            OkButton.Content = "Yeniden adlandır";
        }

        SourceInitialized += (_, _) => ModernDialog.ApplyRoundedCorners(this);
        NameTextBox.TextChanged += (_, _) => ErrorText.Visibility = Visibility.Collapsed;
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();

            var extension = Path.GetExtension(currentName);
            if (!string.IsNullOrEmpty(extension) && !isNew)
            {
                NameTextBox.Select(0, currentName.Length - extension.Length);
            }
            else
            {
                NameTextBox.SelectAll();
            }
        };
    }

    private void ShowError(string message)
    {
        ErrorText.Text = message;
        ErrorText.Visibility = Visibility.Visible;
        NameTextBox.Focus();
    }

    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var newName = NameTextBox.Text.Trim();

        if (string.IsNullOrEmpty(newName))
        {
            ShowError("Ad boş olamaz.");
            return;
        }

        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (newName.IndexOfAny(invalidChars) >= 0)
        {
            ShowError("Ad şu karakterleri içeremez: \\ / : * ? \" < > |");
            return;
        }

        NewName = newName;
        DialogResult = true;
        Close();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        DialogResult = false;
        Close();
    }

    private void Window_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key == Key.Escape)
        {
            DialogResult = false;
            Close();
        }
    }

    private void Window_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.OriginalSource is not System.Windows.Controls.Primitives.ButtonBase &&
            e.OriginalSource is not System.Windows.Controls.TextBox &&
            e.OriginalSource is not System.Windows.Controls.ScrollViewer)
        {
            try { DragMove(); } catch (InvalidOperationException) { }
        }
    }
}
