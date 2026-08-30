namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public interface IUsnIdentityProbe
{
    bool TryReadIdentity(string path, out UsnNodeIdentity identity);
}

public readonly record struct UsnNodeIdentity(
    ulong VolumeSerialNumber,
    UsnFileReference FileReference);
