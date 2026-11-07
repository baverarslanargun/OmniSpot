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

    public static bool UsesMft(AppSettings settings, string? methodOverride = null) =>
        methodOverride?.ToLowerInvariant() switch
        {
            "mft" => true,
            "directory" => false,
            _ => settings.IndexingMethod == IndexScanMethod.Mft
        };
}
