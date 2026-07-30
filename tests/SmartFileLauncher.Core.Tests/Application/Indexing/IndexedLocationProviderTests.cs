using SmartFileLauncher.Core.Application.Indexing;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class IndexedLocationProviderTests
{
    [Fact]
    public void ResolveUsesOneDriveDesktopFallbackAndDeduplicatesRoots()
    {
        var userProfile = @"C:\Users\person";
        var oneDriveDesktop = Path.Combine(userProfile, "OneDrive", "Desktop");
        var downloads = Path.Combine(userProfile, "Downloads");
        var existing = new HashSet<string>(StringComparer.OrdinalIgnoreCase)
        {
            oneDriveDesktop,
            downloads
        };
        var folders = new Dictionary<Environment.SpecialFolder, string>
        {
            [Environment.SpecialFolder.UserProfile] = userProfile,
            [Environment.SpecialFolder.Desktop] = string.Empty,
            [Environment.SpecialFolder.MyDocuments] = downloads,
            [Environment.SpecialFolder.MyPictures] = string.Empty,
            [Environment.SpecialFolder.MyMusic] = string.Empty,
            [Environment.SpecialFolder.MyVideos] = string.Empty
        };
        var provider = new IndexedLocationProvider(
            folder => folders[folder],
            existing.Contains);

        var locations = provider.Resolve();

        Assert.Equal(oneDriveDesktop, locations.DesktopPath);
        Assert.Equal(new[] { oneDriveDesktop, downloads }, locations.RootPaths);
    }

    [Fact]
    public void ResolveKeepsUnavailableDesktopOutOfRoots()
    {
        var userProfile = @"C:\Users\person";
        var provider = new IndexedLocationProvider(
            folder => folder == Environment.SpecialFolder.UserProfile
                ? userProfile
                : string.Empty,
            path => false);

        var locations = provider.Resolve();

        Assert.Equal(Path.Combine(userProfile, "Desktop"), locations.DesktopPath);
        Assert.Empty(locations.RootPaths);
    }
}
