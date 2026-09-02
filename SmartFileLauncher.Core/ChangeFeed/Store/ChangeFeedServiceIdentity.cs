using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

public static class ChangeFeedServiceIdentity
{
    public const string ServiceName = "OmniSpotChangeFeed";

    private const string ServiceSidAuthority = "S-1-5-80";
    private const int ServiceSidPartCount = 5;

    public static string DeriveServiceSid(string serviceName = ServiceName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(serviceName);

        var digest = SHA1.HashData(Encoding.Unicode.GetBytes(serviceName.ToUpperInvariant()));
        var parts = new string[ServiceSidPartCount];

        for (var index = 0; index < ServiceSidPartCount; index++)
        {
            parts[index] = BitConverter
                .ToUInt32(digest, index * sizeof(uint))
                .ToString(CultureInfo.InvariantCulture);
        }

        return $"{ServiceSidAuthority}-{string.Join('-', parts)}";
    }
}
