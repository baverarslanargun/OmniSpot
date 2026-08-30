namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>Reads the volume and file identity behind a path.</summary>
public interface IUsnIdentityProbe
{
    /// <summary>
    /// Returns <see langword="false"/> when the path cannot be opened, which the
    /// feed treats as an unavailable root rather than an error.
    /// </summary>
    bool TryReadIdentity(string path, out UsnNodeIdentity identity);
}

/// <summary>Volume serial number plus file identity of a single path.</summary>
public readonly record struct UsnNodeIdentity(
    ulong VolumeSerialNumber,
    UsnFileReference FileReference);
