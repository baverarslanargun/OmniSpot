using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

[SupportedOSPlatform("windows")]
internal sealed class ChangeFeedStorePermissionProfile
{
    private const FileSystemRights FullRights = FileSystemRights.FullControl;

    private const FileSystemRights DelegatedRights =
        FileSystemRights.Modify | FileSystemRights.Synchronize;

    private ChangeFeedStorePermissionProfile(
        string name,
        IReadOnlyDictionary<SecurityIdentifier, FileSystemRights> rights,
        IReadOnlySet<SecurityIdentifier> acceptableOwners)
    {
        Name = name;
        Rights = rights;
        AcceptableOwners = acceptableOwners;
    }

    public string Name { get; }

    public IReadOnlyDictionary<SecurityIdentifier, FileSystemRights> Rights { get; }

    public IReadOnlySet<SecurityIdentifier> AcceptableOwners { get; }

    public static ChangeFeedStorePermissionProfile ForOwner(string ownerSid)
    {
        var owner = new SecurityIdentifier(ownerSid);
        var system = LocalSystem;
        var administrators = Administrators;

        var rights = new Dictionary<SecurityIdentifier, FileSystemRights>
        {
            [system] = FullRights,
            [administrators] = FullRights
        };

        rights[owner] = rights.ContainsKey(owner) ? FullRights : DelegatedRights;

        return new ChangeFeedStorePermissionProfile(
            "sahip",
            rights,
            new HashSet<SecurityIdentifier> { owner, system, administrators });
    }

    public static ChangeFeedStorePermissionProfile ForTrustedStore(string servicePrincipalSid)
    {
        var principal = new SecurityIdentifier(servicePrincipalSid);
        var administrators = Administrators;

        var rights = new Dictionary<SecurityIdentifier, FileSystemRights>
        {
            [principal] = FullRights,
            [administrators] = FullRights
        };

        return new ChangeFeedStorePermissionProfile(
            "güvenilir",
            rights,
            new HashSet<SecurityIdentifier> { principal, LocalSystem, administrators });
    }

    private static SecurityIdentifier LocalSystem =>
        new(WellKnownSidType.LocalSystemSid, null);

    private static SecurityIdentifier Administrators =>
        new(WellKnownSidType.BuiltinAdministratorsSid, null);
}
