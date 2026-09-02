using System.Globalization;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public interface IUsnIdentityProbe
{
    bool TryReadIdentity(string path, out UsnNodeIdentity identity);
}

public readonly record struct UsnNodeIdentity(
    ulong VolumeSerialNumber,
    UsnFileReference FileReference)
{
    public ChangeFeedRootIdentity ToChangeFeedRootIdentity() =>
        new(
            string.Create(
                CultureInfo.InvariantCulture,
                $"ntfs-vsn:0x{VolumeSerialNumber:X16}"),
            FileReference.ToString());
}
