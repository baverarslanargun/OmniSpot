using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedStoreSecurityTests
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;

    private static readonly SecurityIdentifier System =
        new(WellKnownSidType.LocalSystemSid, null);

    private static readonly SecurityIdentifier Administrators =
        new(WellKnownSidType.BuiltinAdministratorsSid, null);

    private static readonly SecurityIdentifier Everyone =
        new(WellKnownSidType.WorldSid, null);

    [Fact]
    public void EnsureCreated_GivesTheOwnerDirectoryItsOwnAccessList()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);

        layout.EnsureCreated();

        var security = new DirectoryInfo(layout.OwnerDirectory)
            .GetAccessControl(AccessControlSections.Access);

        Assert.True(security.AreAccessRulesProtected);

        var rules = Rules(security);
        Assert.Equal(3, rules.Count);
        Assert.Equal(FileSystemRights.FullControl, rules[System]);
        Assert.Equal(FileSystemRights.FullControl, rules[Administrators]);
        Assert.Equal(
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            rules[new SecurityIdentifier(OwnerSid)]);
    }

    [Fact]
    public void EnsureCreated_LetsQueueFilesInheritThatAccessList()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        layout.EnsureCreated();

        var entry = Path.Combine(layout.QueueDirectory, "girdi.json");
        File.WriteAllText(entry, "{}");

        var rules = Rules(new FileInfo(entry).GetAccessControl(AccessControlSections.Access));

        Assert.Equal(3, rules.Count);
        Assert.False(rules.ContainsKey(Everyone));
        Assert.Equal(
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            rules[new SecurityIdentifier(OwnerSid)]);
    }

    [Fact]
    public void EnsureCreated_RefusesADirectorySomebodyElseCreatedFirst()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        Directory.CreateDirectory(layout.OwnerDirectory);

        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(layout.EnsureCreated);
        Assert.Contains("devralıyor", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCreated_RefusesADirectoryThatGainedAnExtraPermission()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        layout.EnsureCreated();

        var info = new DirectoryInfo(layout.OwnerDirectory);
        var security = info.GetAccessControl(AccessControlSections.Access);
        security.AddAccessRule(new FileSystemAccessRule(
            Everyone,
            FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None,
            AccessControlType.Allow));
        info.SetAccessControl(security);

        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(layout.EnsureCreated);
        Assert.Contains(Everyone.Value, failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void EnsureCreated_RefusesADirectorySquattedWithACorrectLookingAccessList()
    {
        using var directory = new TemporaryDirectory();
        const string foreignSid = "S-1-5-21-9-9-9-4242";
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, foreignSid);

        var inherit = InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;
        var squatted = new DirectorySecurity();
        squatted.SetAccessRuleProtection(true, false);
        squatted.AddAccessRule(new FileSystemAccessRule(
            System, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        squatted.AddAccessRule(new FileSystemAccessRule(
            Administrators, FileSystemRights.FullControl, inherit, PropagationFlags.None, AccessControlType.Allow));
        squatted.AddAccessRule(new FileSystemAccessRule(
            new SecurityIdentifier(foreignSid),
            FileSystemRights.Modify | FileSystemRights.Synchronize,
            inherit,
            PropagationFlags.None,
            AccessControlType.Allow));

        new DirectoryInfo(layout.OwnerDirectory).Create(squatted);

        var failure = Assert.Throws<ChangeFeedStoreSecurityException>(layout.EnsureCreated);
        Assert.Contains("nesne sahibi", failure.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Store_RefusesToOpenWhenTheOwnerDirectoryIsNotProtected()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        Directory.CreateDirectory(layout.OwnerDirectory);

        Assert.Throws<ChangeFeedStoreSecurityException>(
            () => new FileSystemChangeFeedStore(layout));
    }

    [Fact]
    public void ForOwner_RejectsAnOwnerNameThatIsNotASecurityIdentifier()
    {
        Assert.Throws<ArgumentException>(
            () => ChangeFeedStoreLayout.ForOwner(@"C:\Depo", "baver"));
    }

    [Fact]
    public void EnumerateOwners_IgnoresADirectoryThatIsNotASecurityIdentifier()
    {
        using var directory = new TemporaryDirectory();
        ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid).EnsureCreated();
        Directory.CreateDirectory(Path.Combine(directory.Path, "gecici"));

        Assert.Equal(
            new[] { OwnerSid },
            ChangeFeedStoreLayout.EnumerateOwners(directory.Path));
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
