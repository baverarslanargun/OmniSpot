using System.IO;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.ComponentModel;
using SmartFileLauncher.Core.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

[SupportedOSPlatform("windows")]
internal static class ChangeFeedStoreSecurity
{
    private const InheritanceFlags SubtreeInheritance =
        InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit;

    public static void Create(string directory, ChangeFeedStorePermissionProfile profile)
    {
        try
        {
            new DirectoryInfo(directory).Create(BuildSecurity(profile));
        }
        catch (Exception failure)
            when (failure is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} korumalı erişim listesiyle oluşturulamadı.",
                failure);
        }
    }

    public static void Verify(string directory, ChangeFeedStorePermissionProfile profile)
    {
        RejectRedirectedPath(directory);

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
            !profile.AcceptableOwners.Contains(owner))
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} güvenilmeyen bir nesne sahibi taşıyor; sahip örtük olarak " +
                "erişim listesini yeniden yazabilir.");
        }

        if (!security.AreAccessRulesProtected)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} üst dizinden izin devralıyor; {profile.Name} dizini yalnız kendi listesini taşımalıdır.");
        }

        var satisfied = new HashSet<SecurityIdentifier>();

        foreach (FileSystemAccessRule rule in
                 security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            var identity = (SecurityIdentifier)rule.IdentityReference;

            if (rule.AccessControlType != AccessControlType.Allow ||
                !profile.Rights.TryGetValue(identity, out var rights) ||
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

        if (satisfied.Count != profile.Rights.Count)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} zorunlu izinlerin tamamını taşımıyor.");
        }
    }

    public static string RequireLocalFullyQualifiedPath(string directory)
    {
        if (string.IsNullOrWhiteSpace(directory))
        {
            throw new ChangeFeedStoreSecurityException(
                "Depo yolu boş olamaz.");
        }

        if (!Path.IsPathFullyQualified(directory))
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} tam nitelikli bir yol değil; ortam dizinine göre çözülemez.");
        }

        string canonical;
        try
        {
            canonical = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        }
        catch (Exception failure)
            when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} kanonikleştirilemedi.",
                failure);
        }

        if (canonical.StartsWith(@"\\", StringComparison.Ordinal))
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} ağ yoluna çözülüyor ({canonical}); depo yalnız yerel birimde durabilir.");
        }

        var volume = Path.GetPathRoot(canonical);
        if (string.IsNullOrEmpty(volume) || volume.Length < 2 || volume[1] != Path.VolumeSeparatorChar)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} bir yerel birim kökü taşımıyor ({canonical}).");
        }

        try
        {
            if (new DriveInfo(volume).DriveType == DriveType.Network)
            {
                throw new ChangeFeedStoreSecurityException(
                    $"{directory} ağ birimi üzerinde ({volume}); depo yalnız yerel birimde durabilir.");
            }
        }
        catch (Exception failure) when (failure is ArgumentException or IOException)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} biriminin türü belirlenemedi ({volume}).",
                failure);
        }

        return canonical;
    }

    public static void RejectRedirectedPath(string directory)
    {
        RequireLocalFullyQualifiedPath(directory);

        string? redirected;
        try
        {
            redirected = FileSystemPathGuard.Default.FindReparsePointInExistingPath(directory);
        }
        catch (Exception failure)
            when (failure is ArgumentException or UnauthorizedAccessException or IOException)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} yol zinciri doğrulanamadı.",
                failure);
        }

        if (redirected is not null)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} yol zincirinde bir yeniden ayrıştırma noktası var ({redirected}); " +
                "depo başka bir konuma yönlendirilemez.");
        }
    }

    public static void RejectPathOutsideAnchor(string directory, string anchor)
    {
        string resolvedDirectory;
        string resolvedAnchor;
        try
        {
            resolvedDirectory = FileSystemPathGuard.Default.ResolvePhysicalPath(directory);
            resolvedAnchor = FileSystemPathGuard.Default.ResolvePhysicalPath(anchor);
        }
        catch (Exception failure)
            when (failure is ArgumentException or DirectoryNotFoundException
                      or UnauthorizedAccessException or IOException or Win32Exception)
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} fiziksel yolu çözülemedi.",
                failure);
        }

        if (!string.Equals(resolvedDirectory, resolvedAnchor, StringComparison.OrdinalIgnoreCase) &&
            !resolvedDirectory.StartsWith(
                resolvedAnchor + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ChangeFeedStoreSecurityException(
                $"{directory} fiziksel olarak {anchor} altında değil ({resolvedDirectory}).");
        }
    }

    private static DirectorySecurity BuildSecurity(ChangeFeedStorePermissionProfile profile)
    {
        var security = new DirectorySecurity();
        security.SetAccessRuleProtection(true, false);

        foreach (var pair in profile.Rights)
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
}
