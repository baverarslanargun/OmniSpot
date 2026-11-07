using System.IO;
using System.Security.Cryptography;
using System.Text.Json;
using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Input;
using SmartFileLauncher.UI.Services;

namespace SmartFileLauncher.UI.Views;

public partial class SettingsWindow
{
    private bool _celerisKeyFromEnvironment;
    private bool _groqKeyFromEnvironment;

    private void InitializeAiCredentials()
    {
        var credentials = AiCredentials.Load();
        _celerisKeyFromEnvironment = (Environment.GetEnvironmentVariable("OMNISPOT_CELERIS_API_KEY") ??
                                      Environment.GetEnvironmentVariable("CELERIS_API_KEY")) != null;
        _groqKeyFromEnvironment = (Environment.GetEnvironmentVariable("OMNISPOT_GROQ_KEYWORD_API_KEY") ??
                                   Environment.GetEnvironmentVariable("OMNISPOT_GROQ_API_KEY")) != null;
        InitializeAiKeyRow(CelerisKeyStatus, CelerisChangeKeyButton, CelerisKeyHint,
            !string.IsNullOrWhiteSpace(credentials.IntentApiKey), _celerisKeyFromEnvironment);
        InitializeAiKeyRow(GroqKeyStatus, GroqChangeKeyButton, GroqKeyHint,
            !string.IsNullOrWhiteSpace(credentials.KeywordApiKey), _groqKeyFromEnvironment);
    }

    private void InitializeAiKeyRow(TextBlock status, System.Windows.Controls.Button editButton,
        TextBlock hint, bool hasKey, bool fromEnvironment)
    {
        status.Text = fromEnvironment
            ? hasKey ? "Ortam değişkeninden yükleniyor" : "Ortam değişkeni boş"
            : hasKey ? "Anahtar kayıtlı" : "Henüz anahtar eklenmedi";
        editButton.Content = fromEnvironment ? "Yerel anahtar ekle" : hasKey ? "Değiştir" : "Anahtar ekle";
        var provider = ReferenceEquals(editButton, CelerisChangeKeyButton) ? "Celeris" : "Groq";
        AutomationProperties.SetName(editButton, $"{provider}: {editButton.Content}");
        hint.Text = fromEnvironment
            ? "Ortam değişkeni önceliklidir. Yerel kayıt, bu değişken kaldırıldığında kullanılır."
            : hasKey ? "Kaydettiğinizde mevcut yerel anahtar değişir." : "Anahtar yeniden başlatınca kullanılır.";
    }

    private void ChangeCelerisKey_Click(object sender, RoutedEventArgs e) => SetAiKeyEditing(true, true);
    private void ChangeGroqKey_Click(object sender, RoutedEventArgs e) => SetAiKeyEditing(false, true);
    private void CancelCelerisKey_Click(object sender, RoutedEventArgs e) => SetAiKeyEditing(true, false);
    private void CancelGroqKey_Click(object sender, RoutedEventArgs e) => SetAiKeyEditing(false, false);

    private void SaveCelerisKey_Click(object sender, RoutedEventArgs e) =>
        SaveAiKey(true);

    private void SaveGroqKey_Click(object sender, RoutedEventArgs e) =>
        SaveAiKey(false);

    private void SetAiKeyEditing(bool celeris, bool editing)
    {
        var input = celeris ? CelerisApiKey : GroqApiKey;
        var editor = celeris ? CelerisKeyEditor : GroqKeyEditor;
        var editButton = celeris ? CelerisChangeKeyButton : GroqChangeKeyButton;
        var error = celeris ? CelerisKeyError : GroqKeyError;
        input.Clear();
        error.Text = string.Empty;
        editor.Visibility = editing ? Visibility.Visible : Visibility.Collapsed;
        editButton.Visibility = editing ? Visibility.Hidden : Visibility.Visible;
        if (editing) input.Focus();
        else editButton.Focus();
    }

    private bool TryCancelAiKeyEdit()
    {
        if (CelerisKeyEditor.IsKeyboardFocusWithin) SetAiKeyEditing(true, false);
        else if (GroqKeyEditor.IsKeyboardFocusWithin) SetAiKeyEditing(false, false);
        else return false;
        return true;
    }

    private void AiKeyInput_KeyDown(object sender, System.Windows.Input.KeyEventArgs e)
    {
        if (e.Key != Key.Enter) return;
        SaveAiKey(ReferenceEquals(sender, CelerisApiKey));
        e.Handled = true;
    }

    private void SaveAiKey(bool celeris)
    {
        var input = celeris ? CelerisApiKey : GroqApiKey;
        var status = celeris ? CelerisKeyStatus : GroqKeyStatus;
        var error = celeris ? CelerisKeyError : GroqKeyError;
        var editButton = celeris ? CelerisChangeKeyButton : GroqChangeKeyButton;
        var hint = celeris ? CelerisKeyHint : GroqKeyHint;
        var fromEnvironment = celeris ? _celerisKeyFromEnvironment : _groqKeyFromEnvironment;
        using var key = input.SecurePassword;
        if (key.Length == 0)
        {
            error.Text = "Bir anahtar girin.";
            input.Focus();
            return;
        }

        try
        {
            if (celeris) AiCredentialStore.SaveCeleris(key);
            else AiCredentialStore.SaveGroq(key);
            InitializeAiKeyRow(status, editButton, hint, true, fromEnvironment);
            status.Text = fromEnvironment
                ? "Yerel anahtar kaydedildi; ortam değişkeni öncelikli."
                : "Yeni anahtar kaydedildi";
            if (!fromEnvironment) AiKeysRestartNotice.Visibility = Visibility.Visible;
            SetAiKeyEditing(celeris, false);
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                                        InvalidOperationException or ArgumentException or CryptographicException or
                                        System.Security.SecurityException or FormatException)
        {
            error.Text = "Anahtar kaydedilemedi. Yerel anahtar dosyasını ve erişim izinlerini kontrol edin.";
        }
    }
}
