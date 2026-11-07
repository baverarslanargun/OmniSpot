using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartFileLauncher.UI.Services;

internal sealed record AiCredentials(string? IntentApiKey, string? KeywordApiKey)
{
    internal static AiCredentials Load(Action<string>? log = null)
    {
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniSpot");
        return new AiCredentials(
            Environment.GetEnvironmentVariable("OMNISPOT_CELERIS_API_KEY") ??
            Environment.GetEnvironmentVariable("CELERIS_API_KEY") ??
            ReadKey(Path.Combine(directory, "celeris-key.json"), "apiKey", log),
            Environment.GetEnvironmentVariable("OMNISPOT_GROQ_KEYWORD_API_KEY") ??
            Environment.GetEnvironmentVariable("OMNISPOT_GROQ_API_KEY") ??
            ReadKey(Path.Combine(directory, "groq-keys.json"), "keywordApiKey", log));
    }

    internal static string? ReadKey(string path, string field, Action<string>? log = null)
    {
        try
        {
            if (!File.Exists(path)) return null;
            using var document = JsonDocument.Parse(File.ReadAllText(path));
            var root = document.RootElement;
            if (root.GetProperty("version").GetInt32() != 1)
                throw new InvalidDataException();
            var encrypted = Convert.FromHexString(root.GetProperty(field).GetString()!);
            var bytes = ProtectedData.Unprotect(encrypted, null, DataProtectionScope.CurrentUser);
            try
            {
                var key = Encoding.Unicode.GetString(bytes);
                return string.IsNullOrWhiteSpace(key) ? null : key;
            }
            finally
            {
                CryptographicOperations.ZeroMemory(bytes);
            }
        }
        catch (Exception ex) when (ex is IOException or InvalidDataException or UnauthorizedAccessException or JsonException or
                                   KeyNotFoundException or InvalidOperationException or ArgumentException or
                                   FormatException or CryptographicException)
        {
            log?.Invoke("AI anahtar dosyası okunamadı; ilgili sağlayıcı yapılandırılmamış kabul edildi.");
            return null;
        }
    }
}
