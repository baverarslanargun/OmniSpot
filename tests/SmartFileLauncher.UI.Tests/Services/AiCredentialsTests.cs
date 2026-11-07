using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class AiCredentialsTests
{
    [Fact]
    public void ReadsCurrentUserDpapiKeyWithoutLoggingIt()
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        const string key = "test-only-api-key";
        var encrypted = ProtectedData.Protect(Encoding.Unicode.GetBytes(key), null, DataProtectionScope.CurrentUser);
        try
        {
            File.WriteAllText(path, JsonSerializer.Serialize(new { version = 1, apiKey = Convert.ToHexString(encrypted) }));
            var messages = new List<string>();
            Assert.Equal(key, AiCredentials.ReadKey(path, "apiKey", messages.Add));
            Assert.Empty(messages);
        }
        finally
        {
            File.Delete(path);
        }
    }

    [Theory]
    [InlineData("{\"version\":1,\"apiKey\":\"not-encrypted-secret\"}")]
    [InlineData("{\"version\":2,\"apiKey\":\"not-encrypted-secret\"}")]
    [InlineData("{\"version\":1}")]
    [InlineData("invalid-json")]
    public void InvalidCredentialFileLeavesProviderUnconfiguredAndDoesNotLogContents(string contents)
    {
        var path = Path.Combine(Path.GetTempPath(), Guid.NewGuid() + ".json");
        try
        {
            File.WriteAllText(path, contents);
            var messages = new List<string>();
            Assert.Null(AiCredentials.ReadKey(path, "apiKey", messages.Add));
            Assert.DoesNotContain("secret", Assert.Single(messages));
        }
        finally
        {
            File.Delete(path);
        }
    }
}
