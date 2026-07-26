using System.IO;
using System.Windows;

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
            Title = "Yeni Oluştur";
            LabelText.Text = "Ad girin:";
        }
        else
        {
            Title = "Yeniden Adlandır";
            LabelText.Text = "Yeni ad girin:";
        }
        
        // Dosya uzantısı hariç seç
        Loaded += (_, _) =>
        {
            NameTextBox.Focus();
            
            var extension = Path.GetExtension(currentName);
            if (!string.IsNullOrEmpty(extension) && !isNew)
            {
                // Uzantı hariç seç
                NameTextBox.Select(0, currentName.Length - extension.Length);
            }
            else
            {
                NameTextBox.SelectAll();
            }
        };
    }
    
    private void OK_Click(object sender, RoutedEventArgs e)
    {
        var newName = NameTextBox.Text.Trim();
        
        if (string.IsNullOrEmpty(newName))
        {
            System.Windows.MessageBox.Show("Ad boş olamaz.", "Hata", MessageBoxButton.OK, MessageBoxImage.Warning);
            return;
        }
        
        // Geçersiz karakterleri kontrol et
        char[] invalidChars = Path.GetInvalidFileNameChars();
        if (newName.IndexOfAny(invalidChars) >= 0)
        {
            System.Windows.MessageBox.Show(
                "Ad şu karakterleri içeremez: \\ / : * ? \" < > |",
                "Hata",
                MessageBoxButton.OK,
                MessageBoxImage.Warning);
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
}
