using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.Core.Application.Indexing;

public enum IndexVolumeKind
{
    Unknown,
    Ntfs,
    Other
}

public interface IIndexVolumePolicy
{
    IndexVolumeMethod MethodFor(string volume);
    IndexVolumeKind KindOf(string volume);
}

public sealed class SettingsIndexVolumePolicy(AppSettings settings, string? methodOverride = null) : IIndexVolumePolicy
{
    public IndexVolumeMethod MethodFor(string volume) => SettingsIndexedLocationProvider.MethodFor(settings, volume, methodOverride);
    public IndexVolumeKind KindOf(string volume) => IndexVolumeProbe.KindOf(volume);
}

public static class IndexVolumeProbe
{
    public static IndexVolumeKind KindOf(string volume)
    {
        if (string.IsNullOrWhiteSpace(volume) || volume.StartsWith(@"\\", StringComparison.Ordinal)) return IndexVolumeKind.Other;
        try
        {
            var drive = new DriveInfo(volume);
            if (drive.DriveType is DriveType.Network or DriveType.CDRom) return IndexVolumeKind.Other;
            if (drive.DriveType == DriveType.NoRootDirectory || !drive.IsReady) return IndexVolumeKind.Unknown;
            return drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase) ? IndexVolumeKind.Ntfs : IndexVolumeKind.Other;
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or ArgumentException)
        {
            return IndexVolumeKind.Unknown;
        }
    }

    public static bool AllowsMft(string volume) => KindOf(volume) != IndexVolumeKind.Other;
}
