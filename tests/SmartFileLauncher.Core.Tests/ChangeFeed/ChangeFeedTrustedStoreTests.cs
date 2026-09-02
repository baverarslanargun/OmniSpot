using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedTrustedStoreTests
{
    private const string OwnerSid = "S-1-5-21-9-9-9-1001";

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier LocalSystem =
        new(WellKnownSidType.LocalSystemSid, null);

    private static readonly SecurityIdentifier Everyone =
        new(WellKnownSidType.WorldSid, null);

    private static readonly SecurityIdentifier Users =
        new(WellKnownSidType.BuiltinUsersSid, null);

    [Fact]
    public void DeriveServiceSid_MatchesWhatWindowsResolvesForAnInstalledService()
    {
        string[] candidates = { "Spooler", "EventLog", "Dnscache", "Schedule" };
        var compared = 0;

        foreach (var candidate in candidates)
        {
            SecurityIdentifier resolved;
            try
            {
                resolved = (SecurityIdentifier)new NTAccount("NT SERVICE", candidate)
                    .Translate(typeof(SecurityIdentifier));
            }
            catch (IdentityNotMappedException)
            {
                continue;
            }

            Assert.Equal(
                resolved.Value,
                ChangeFeedServiceIdentity.DeriveServiceSid(candidate));
            compared++;
        }

        Assert.True(compared > 0, "Karşılaştırılacak kurulu servis bulunamadı.");
    }

    [Fact]
    public void DeriveServiceSid_IsCaseInsensitiveAndStable()
    {
        var expected = ChangeFeedServiceIdentity.DeriveServiceSid(
            ChangeFeedServiceIdentity.ServiceName);

        Assert.Equal(expected, ChangeFeedServiceIdentity.DeriveServiceSid("omnispotchangefeed"));
        Assert.Equal(expected, ChangeFeedServiceIdentity.DeriveServiceSid("OMNISPOTCHANGEFEED"));
        Assert.StartsWith("S-1-5-80-", expected, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedStore_GivesTheServicePrincipalAndAdministratorsNothingElse()
    {
        using var directory = new TemporaryDirectory();
        var principal = TestStoreOwner.Sid;
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(
            Path.Combine(directory.Path, "Depo"),
            OwnerSid,
            principal);

        layout.EnsureCreated();

        var rules = Rules(new DirectoryInfo(layout.OwnerDirectory)
            .GetAccessControl(AccessControlSections.Access));

        Assert.Equal(2, rules.Count);
        Assert.Equal(FileSystemRights.FullControl, rules[new SecurityIdentifier(principal)]);
        Assert.Equal(FileSystemRights.FullControl, rules[Administrators]);
        Assert.False(rules.ContainsKey(new SecurityIdentifier(OwnerSid)));
    }

    [Fact]
    public void TrustedStore_ExcludesTheStoreOwnerAndEveryNormalUser()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(
            Path.Combine(directory.Path, "Depo"),
            OwnerSid,
            TestStoreOwner.Sid);

        layout.EnsureCreated();

        var rules = Rules(new DirectoryInfo(layout.OwnerDirectory)
            .GetAccessControl(AccessControlSections.Access));

        Assert.False(rules.ContainsKey(new SecurityIdentifier(OwnerSid)));
        Assert.False(rules.ContainsKey(Everyone));
        Assert.False(rules.ContainsKey(Users));
        Assert.False(rules.ContainsKey(LocalSystem));
    }

    [Fact]
    public void TrustedStore_HardensTheStoreRootItself()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "Depo");
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(root, OwnerSid, TestStoreOwner.Sid);

        layout.EnsureCreated();

        var security = new DirectoryInfo(root)
            .GetAccessControl(AccessControlSections.Access);

        Assert.True(security.AreAccessRulesProtected);
        var rules = Rules(security);
        Assert.Equal(2, rules.Count);
        Assert.Equal(FileSystemRights.FullControl, rules[new SecurityIdentifier(TestStoreOwner.Sid)]);
        Assert.Equal(FileSystemRights.FullControl, rules[Administrators]);
    }

    [Fact]
    public void TrustedStore_RefusesAStoreRootSomebodyElseCreatedFirst()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "Depo");
        Directory.CreateDirectory(root);

        var layout = ChangeFeedStoreLayout.ForTrustedOwner(root, OwnerSid, TestStoreOwner.Sid);

        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(layout.EnsureCreated);
        Assert.Contains("devralıyor", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedStore_RefusesADirectoryCarryingTheOwnerProfile()
    {
        using var directory = new TemporaryDirectory();
        var root = Path.Combine(directory.Path, "Depo");

        ChangeFeedStoreLayout
            .ForTrustedOwner(root, "S-1-5-21-9-9-9-2002", TestStoreOwner.Sid)
            .EnsureCreated();

        ChangeFeedStoreLayout.ForOwner(root, TestStoreOwner.Sid).EnsureCreated();

        var trusted = ChangeFeedStoreLayout.ForTrustedOwner(
            root,
            TestStoreOwner.Sid,
            TestStoreOwner.Sid);

        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(trusted.EnsureCreated);
        Assert.Contains("beklenmeyen bir izin", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void ForTrustedOwner_RejectsAPrincipalThatIsNotASecurityIdentifier()
    {
        Assert.Throws<ArgumentException>(
            () => ChangeFeedStoreLayout.ForTrustedOwner(@"C:\Depo", OwnerSid, "OmniSpotChangeFeed"));
    }

    [Fact]
    public void TrustedRoot_IsNotTheLegacyRoot()
    {
        Assert.NotEqual(ChangeFeedStoreLayout.LegacyRoot, ChangeFeedStoreLayout.DefaultTrustedRoot);
        Assert.EndsWith(
            ChangeFeedStoreLayout.TrustedStoreFolderName,
            ChangeFeedStoreLayout.DefaultTrustedRoot,
            StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedStore_RefusesAStoreRootThatIsAJunction()
    {
        using var directory = new TemporaryDirectory();
        var target = directory.CreateDirectory("hedef");
        var junction = Path.Combine(directory.Path, "baglanti");

        Assert.True(TryCreateJunction(junction, target), "Bağlantı kurulamadı.");

        var layout = ChangeFeedStoreLayout.ForTrustedOwner(junction, OwnerSid, TestStoreOwner.Sid);

        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(layout.EnsureCreated);
        Assert.Contains("yeniden ayrıştırma", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedStore_RefusesAnAncestorJunctionEvenWhenTheLeafLooksNormal()
    {
        using var directory = new TemporaryDirectory();
        var target = directory.CreateDirectory("hedef");
        var ancestor = Path.Combine(directory.Path, "ust");
        Assert.True(TryCreateJunction(ancestor, target), "Bağlantı kurulamadı.");

        var root = Path.Combine(ancestor, "Depo");
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(root, OwnerSid, TestStoreOwner.Sid);

        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(layout.EnsureCreated);
        Assert.Contains("yol zincirinde", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void TrustedStore_CreatesNothingBeforeRejectingAnAncestorJunction()
    {
        using var directory = new TemporaryDirectory();
        var target = directory.CreateDirectory("hedef");
        var ancestor = Path.Combine(directory.Path, "ust");
        Assert.True(TryCreateJunction(ancestor, target), "Bağlantı kurulamadı.");

        var root = Path.Combine(ancestor, "Depo");
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(root, OwnerSid, TestStoreOwner.Sid);

        Assert.Throws<ChangeFeedStoreSecurityException>(layout.EnsureCreated);

        Assert.Empty(Directory.GetFileSystemEntries(target));
        Assert.False(Directory.Exists(Path.Combine(target, "Depo")));
    }

    [Fact]
    public void TrustedStore_RefusesAForwardSlashUncPath()
    {
        var layout = ChangeFeedStoreLayout.ForTrustedOwner(
            "//sunucu/pay/Depo",
            OwnerSid,
            TestStoreOwner.Sid);

        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(layout.EnsureCreated);
        Assert.Contains("ağ yoluna çözülüyor", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void DefaultTrustedRoot_IsPinnedToTheSystemProfileNotTheCallersProfile()
    {
        var root = ChangeFeedStoreLayout.DefaultTrustedRoot;

        Assert.True(Path.IsPathFullyQualified(root));
        Assert.Contains(@"\config\systemprofile\", root, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            root,
            StringComparison.OrdinalIgnoreCase);
        Assert.NotEqual(ChangeFeedStoreLayout.LegacyRoot, root);
    }

    [Fact]
    public void ForTrustedOwner_RefusesToOpenOutsideLocalSystem()
    {
        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(
            () => ChangeFeedStoreLayout.ForTrustedOwner(OwnerSid));

        Assert.Contains("LocalSystem", failure.Message, StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("//sunucu/pay/Depo")]
    [InlineData(@"\sunucu\pay\Depo")]
    [InlineData(@"\OmniSpot")]
    [InlineData("C:OmniSpot")]
    [InlineData(@"gorece\yol")]
    [InlineData("")]
    [InlineData("   ")]
    public void RequireLocalFullyQualifiedPath_RefusesEverythingThatIsNotALocalAbsolutePath(string path)
    {
        Assert.Throws<ChangeFeedStoreSecurityException>(
            () => ChangeFeedStoreSecurity.RequireLocalFullyQualifiedPath(path));
    }

    [Fact]
    public void RequireLocalFullyQualifiedPath_AcceptsALocalAbsolutePath()
    {
        using var directory = new TemporaryDirectory();

        Assert.Equal(
            Path.TrimEndingDirectorySeparator(directory.Path),
            ChangeFeedStoreSecurity.RequireLocalFullyQualifiedPath(directory.Path));
    }

    [Fact]
    public void LegacyStore_IsOnlyDetectedNeverDeleted()
    {
        using var directory = new TemporaryDirectory();
        var legacy = Path.Combine(
            directory.Path,
            ChangeFeedStoreLayout.ProductFolderName,
            ChangeFeedStoreLayout.StoreFolderName);
        Directory.CreateDirectory(Path.Combine(legacy, "S-1-5-21-1-2-3-1001", "queue"));

        Assert.True(ChangeFeedStoreLayout.LegacyStoreExists(legacy));

        Assert.Null(
            typeof(ChangeFeedStoreLayout).GetMethod(
                "RemoveLegacyStore",
                System.Reflection.BindingFlags.Public |
                System.Reflection.BindingFlags.NonPublic |
                System.Reflection.BindingFlags.Static));

        Assert.True(Directory.Exists(legacy));
    }

    private static bool TryCreateJunction(string link, string target)
    {
        using var process = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
        {
            FileName = "cmd.exe",
            Arguments = $"/c mklink /J \"{link}\" \"{target}\"",
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true
        });

        if (process is null)
        {
            return false;
        }

        process.WaitForExit();
        return process.ExitCode == 0 && Directory.Exists(link);
    }

    private static Dictionary<SecurityIdentifier, FileSystemRights> Rules(
        FileSystemSecurity security)
    {
        var rules = new Dictionary<SecurityIdentifier, FileSystemRights>();

        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            rules[(SecurityIdentifier)rule.IdentityReference] = rule.FileSystemRights;
        }

        return rules;
    }
}
