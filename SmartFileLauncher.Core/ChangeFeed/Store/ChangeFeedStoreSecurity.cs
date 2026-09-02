using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

[SupportedOSPlatform("windows")]
internal static class ChangeFeedStoreSecurity
{
    private const FileSystemRights PrivilegedRights = FileSystemRights.FullControl;

    private const FileSystemRights OwnerRights =
        FileSystemRights.Modify | FileSystemRights.Synchronize;

    private const InheritanceFlags SubtreeInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public static void Create(string directory, string ownerSid)
    {
        try
        {
            new DirectoryInfo(directory).Create(BuildSecurity(ownerSid));
        }
        catch (Exception failure)
            when (failure is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} korumalı erişim listesiyle oluşturulamadı.",
                failure);
        }
    }

    public static void Verify(string directory, string ownerSid)
    {
        DirectorySecurity security;
        try
        {
            security = new DirectoryInfo(directory).GetAccessControl(
                AccessControlSections.Access | AccessControlSections.Owner);
        }
        catch (Exception failure)
            when (failure is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} erişim listesi okunamadı.",
                failure);
        }

        if (security.GetOwner(typeof(SecurityIdentifier)) is not SecurityIdentifier owner ||
            !TrustedOwners(ownerSid).Contains(owner))
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} güvenilmeyen bir nesne sahibi taşıyor; sahip örtük olarak " +
                "erişim listesini yeniden yazabilir.");
        }

        if (!security.AreAccessRulesProtected)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} üst dizinden izin devralıyor; sahip dizini yalnız kendi listesini taşımalıdır.");
        }

        var expected = ExpectedRights(ownerSid);
        var satisfied = new HashSet<SecurityIdentifier>();

        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var identity = (SecurityIdentifier)rule.IdentityReference;

            if (rule.AccessControlType != AccessControlType.Allow ||
                !expected.TryGetValue(identity, out var rights) ||
                rule.FileSystemRights != rights ||
                rule.InheritanceFlags != SubtreeInheritance ||
                rule.PropagationFlags != PropagationFlags.None)
            {
                throw new ChangeFeedStoreSecurityException(
                    $"{directory} beklenmeyen bir izin taşıyor: {identity} " +
                    $"{rule.AccessControlType} {rule.FileSystemRights}.");
            }

            satisfied.Add(identity);
        }

        if (satisfied.Count != expected.Count)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} zorunlu izinlerin tamamını taşımıyor.");
        }
    }

    private static HashSet<SecurityIdentifier> TrustedOwners(string ownerSid) =>
        new()
        {
            new SecurityIdentifier(ownerSid),
            new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null),
            new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null)
        };

    private static DirectorySecurity BuildSecurity(string ownerSid)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);

        foreach (var pair in ExpectedRights(ownerSid))
        {
            security.AddAccessRule(new FileSystemAccessRule(
                pair.Key,
                pair.Value,
                SubtreeInheritance,
                PropagationFlags.None,
                AccessControlType.Allow));
        }

        return security;
    }

    private static Dictionary<SecurityIdentifier, FileSystemRights> ExpectedRights(string ownerSid)
    {
        var owner = new SecurityIdentifier(ownerSid);
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        var administrators = new SecurityIdentifier(WellKnownSidType.BuiltinAdministratorsSid, null);

        var expected = new Dictionary<SecurityIdentifier, FileSystemRights>
        {
            [system] = PrivilegedRights,
            [administrators] = PrivilegedRights
        };

        expected[owner] = expected.ContainsKey(owner) ? PrivilegedRights : OwnerRights;
        return expected;
    }
}
