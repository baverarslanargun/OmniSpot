using System.IO;
using System.Runtime.InteropServices;
using System.Security;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json.Nodes;

namespace SmartFileLauncher.UI.Services;

internal static class AiCredentialStore
{
    internal static void SaveCeleris(SecureString key) => SaveKey("celeris-key.json", "apiKey", key);

    internal static void SaveGroq(SecureString key) => SaveKey("groq-keys.json", "keywordApiKey", key);

    private static void SaveKey(string fileName, string field, SecureString key)
    {
        if (key.Length == 0) throw new ArgumentException("API anahtarı boş olamaz.", nameof(key));
        var directory = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "OmniSpot");
        var path = Path.Combine(directory, fileName);
        var payload = File.Exists(path)
            ? JsonNode.Parse(File.ReadAllText(path)) as JsonObject ?? throw new InvalidDataException()
            : new JsonObject { ["version"] = 1 };
        if (payload["version"]?.GetValue<int>() != 1) throw new InvalidDataException();

        var pointer = Marshal.SecureStringToGlobalAllocUnicode(key);
        var bytes = new byte[key.Length * 2];
        try
        {
            Marshal.Copy(pointer, bytes, 0, bytes.Length);
            var encrypted = Convert.ToHexString(ProtectedData.Protect(bytes, null, DataProtectionScope.CurrentUser));
            payload[field] = encrypted;
            if (field == "keywordApiKey" && !payload.ContainsKey("intentApiKey"))
                payload["intentApiKey"] = encrypted;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(bytes);
            Marshal.ZeroFreeGlobalAllocUnicode(pointer);
        }
        payload["updatedAt"] = DateTimeOffset.Now.ToString("o");

        Directory.CreateDirectory(directory);
        var temporaryPath = Path.Combine(directory, Path.GetRandomFileName());
        try
        {
            using (File.Open(temporaryPath, FileMode.CreateNew, FileAccess.Write, FileShare.None)) { }
            using var identity = WindowsIdentity.GetCurrent();
            var user = identity.User ?? throw new InvalidOperationException();
            var acl = new FileSecurity();
            acl.SetOwner(user);
            acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(user, FileSystemRights.FullControl, AccessControlType.Allow));
            new FileInfo(temporaryPath).SetAccessControl(acl);
            File.WriteAllText(temporaryPath, payload.ToJsonString());
            File.Move(temporaryPath, path, overwrite: true);
        }
        finally
        {
            if (File.Exists(temporaryPath)) File.Delete(temporaryPath);
        }
    }
}
