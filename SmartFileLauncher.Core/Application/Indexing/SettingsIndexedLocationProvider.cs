using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.Core.Application.Indexing;

public sealed class SettingsIndexedLocationProvider(
    AppSettings settings,
    IIndexedLocationProvider fallback) : IIndexedLocationProvider
{
    public IndexLocations Resolve()
    {
        var standard = fallback.Resolve();
        if (settings.IndexedFolders is null) return standard;
        var paths = settings.IndexedFolders
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(path => Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToArray();
        return new IndexLocations(standard.DesktopPath, paths);
    }

    public static string VolumeRoot(string path)
    {
        var root = Path.GetPathRoot(Path.GetFullPath(path));
        if (string.IsNullOrEmpty(root)) throw new ArgumentException("Konumun kökü belirlenemedi.", nameof(path));
        root = root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length == 2 && root[1] == ':') root = char.ToUpperInvariant(root[0]) + ":";
        return root + Path.DirectorySeparatorChar;
    }

    public static IndexVolumeMethod MethodFor(AppSettings settings, string volume, string? methodOverride = null)
    {
        switch (methodOverride?.ToLowerInvariant())
        {
            case "mft": return IndexVolumeMethod.Mft;
            case "directory": return IndexVolumeMethod.Folders;
        }
        var key = VolumeRoot(volume);
        if (settings.IndexVolumeMethods is { } methods)
            foreach (var (path, method) in methods)
                if (Enum.IsDefined(method) && !string.IsNullOrWhiteSpace(path) &&
                    string.Equals(TryVolumeRoot(path), key, StringComparison.OrdinalIgnoreCase))
                    return method;
        return settings.IndexingMethod == IndexScanMethod.Mft ? IndexVolumeMethod.Automatic : IndexVolumeMethod.Folders;
    }

    private static string? TryVolumeRoot(string path)
    {
        try { return VolumeRoot(path); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
