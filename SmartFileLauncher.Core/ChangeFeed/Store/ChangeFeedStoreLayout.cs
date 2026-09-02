using System.IO;
using System.Runtime.Versioning;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

public sealed class ChangeFeedStoreLayout
{
    public const string ProductFolderName = "OmniSpot";
    public const string StoreFolderName = "ChangeFeed";
    public const string SubscriptionFileName = "subscription.json";
    public const string SequenceFileName = "sequence.txt";
    public const string QueueFolderName = "queue";
    public const string StateFolderName = "state";
    public const string TrustedStoreFolderName = "ChangeFeedStore";

    private const string SecurityIdentifierPrefix = "S-1-";

    private ChangeFeedStoreLayout(
        string storeRoot,
        string ownerSid,
        string ownerDirectory,
        string? trustedPrincipalSid)
    {
        StoreRoot = storeRoot;
        OwnerSid = ownerSid;
        TrustedPrincipalSid = trustedPrincipalSid;
        OwnerDirectory = ownerDirectory;
        SubscriptionPath = Path.Combine(ownerDirectory, SubscriptionFileName);
        SequencePath = Path.Combine(ownerDirectory, SequenceFileName);
        QueueDirectory = Path.Combine(ownerDirectory, QueueFolderName);
        StateDirectory = Path.Combine(ownerDirectory, StateFolderName);
    }

    public string StoreRoot { get; }

    public string OwnerSid { get; }

    public string? TrustedPrincipalSid { get; }

    public bool IsTrustedStore => TrustedPrincipalSid is not null;

    public string OwnerDirectory { get; }

    public string SubscriptionPath { get; }

    public string SequencePath { get; }

    public string QueueDirectory { get; }

    public string StateDirectory { get; }

    public static string DefaultRoot =>
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            ProductFolderName,
            StoreFolderName);

    public static string LegacyRoot => DefaultRoot;

    public static string DefaultTrustedRoot
    {
        get
        {
            var windows = Environment.GetEnvironmentVariable("SystemRoot");
            if (string.IsNullOrWhiteSpace(windows))
            {
                throw new ChangeFeedStoreSecurityException(
                    "SystemRoot çözülemedi; güvenilir depo kökü sabitlenemez.");
            }

            return Path.Combine(
                windows,
                "System32",
                "config",
                "systemprofile",
                "AppData",
                "Local",
                ProductFolderName,
                TrustedStoreFolderName);
        }
    }

    public static ChangeFeedStoreLayout ForOwner(string ownerSid) =>
        ForOwner(DefaultRoot, ownerSid);

    public static ChangeFeedStoreLayout ForTrustedOwner(string ownerSid)
    {
        if (!SecurityIsEnforced)
        {
            throw new ChangeFeedStoreSecurityException(
                "Güvenilir depo yalnız Windows üzerinde açılabilir.");
        }

        if (!RunningAsLocalSystem())
        {
            throw new ChangeFeedStoreSecurityException(
                "Güvenilir depo yalnız LocalSystem bağlamında açılabilir.");
        }

        var root = ChangeFeedStoreSecurity.RequireLocalFullyQualifiedPath(DefaultTrustedRoot);

        return ForTrustedOwner(
            root,
            ownerSid,
            ChangeFeedServiceIdentity.DeriveServiceSid());
    }

    internal static ChangeFeedStoreLayout ForTrustedOwner(
        string trustedRoot,
        string ownerSid,
        string servicePrincipalSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(servicePrincipalSid);

        if (!LooksLikeSecurityIdentifier(servicePrincipalSid))
        {
            throw new ArgumentException(
                $"Servis kimliği bir güvenlik kimliği olmalıdır: {servicePrincipalSid}",
                nameof(servicePrincipalSid));
        }

        var layout = ForOwner(trustedRoot, ownerSid);
        return new ChangeFeedStoreLayout(
            layout.StoreRoot,
            layout.OwnerSid,
            layout.OwnerDirectory,
            servicePrincipalSid);
    }

    public static IReadOnlyList<string> EnumerateOwners(string storeRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);

        if (!Directory.Exists(storeRoot))
        {
            return Array.Empty<string>();
        }

        return Directory.GetDirectories(storeRoot)
            .Select(Path.GetFileName)
            .Where(name => !string.IsNullOrWhiteSpace(name))
            .Select(name => name!)
            .Where(LooksLikeSecurityIdentifier)
            .ToArray();
    }

    public static ChangeFeedStoreLayout ForOwner(string storeRoot, string ownerSid)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storeRoot);
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerSid);

        if (ownerSid.AsSpan().IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new ArgumentException(
                $"Sahip kimliği dizin adı olarak kullanılamaz: {ownerSid}",
                nameof(ownerSid));
        }

        if (!LooksLikeSecurityIdentifier(ownerSid))
        {
            throw new ArgumentException(
                $"Sahip kimliği bir güvenlik kimliği olmalıdır: {ownerSid}",
                nameof(ownerSid));
        }

        return new ChangeFeedStoreLayout(
            storeRoot,
            ownerSid,
            Path.Combine(storeRoot, ownerSid),
            null);
    }

    public static bool LegacyStoreExists(string legacyRoot)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(legacyRoot);
        return Directory.Exists(legacyRoot);
    }

    public void EnsureCreated()
    {
        if (SecurityIsEnforced && TrustedPrincipalSid is { } rootPrincipal)
        {
            var rootProfile = ChangeFeedStorePermissionProfile.ForTrustedStore(rootPrincipal);

            ChangeFeedStoreSecurity.RejectRedirectedPath(StoreRoot);

            if (!Directory.Exists(StoreRoot))
            {
                ChangeFeedStoreSecurity.Create(StoreRoot, rootProfile);
            }

            ChangeFeedStoreSecurity.Verify(StoreRoot, rootProfile);
        }
        else
        {
            Directory.CreateDirectory(StoreRoot);
        }

        if (SecurityIsEnforced)
        {
            var profile = TrustedPrincipalSid is { } principal
                ? ChangeFeedStorePermissionProfile.ForTrustedStore(principal)
                : ChangeFeedStorePermissionProfile.ForOwner(OwnerSid);

            ChangeFeedStoreSecurity.RejectRedirectedPath(OwnerDirectory);

            if (!Directory.Exists(OwnerDirectory))
            {
                ChangeFeedStoreSecurity.Create(OwnerDirectory, profile);
            }

            ChangeFeedStoreSecurity.Verify(OwnerDirectory, profile);
            ChangeFeedStoreSecurity.RejectPathOutsideAnchor(OwnerDirectory, StoreRoot);
        }
        else
        {
            Directory.CreateDirectory(OwnerDirectory);
        }

        Directory.CreateDirectory(QueueDirectory);
        Directory.CreateDirectory(StateDirectory);
    }

    [SupportedOSPlatformGuard("windows")]
    private static bool SecurityIsEnforced => OperatingSystem.IsWindows();

    [SupportedOSPlatform("windows")]
    private static bool RunningAsLocalSystem()
    {
        using var identity = System.Security.Principal.WindowsIdentity.GetCurrent();
        return identity.IsSystem;
    }

    private static bool LooksLikeSecurityIdentifier(string value)
    {
        if (!value.StartsWith(SecurityIdentifierPrefix, StringComparison.Ordinal))
        {
            return false;
        }

        var body = value.AsSpan(2);
        var digits = 0;

        foreach (var character in body)
        {
            if (character == '-')
            {
                if (digits == 0)
                {
                    return false;
                }

                digits = 0;
                continue;
            }

            if (!char.IsAsciiDigit(character))
            {
                return false;
            }

            digits++;
        }

        return digits > 0;
    }
}
