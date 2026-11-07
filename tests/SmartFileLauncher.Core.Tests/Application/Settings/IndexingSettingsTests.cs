using System.Text.Json;
using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Settings;

public sealed class IndexingSettingsTests
{
    [Fact]
    public void OldSettingsKeepStandardLocationsAndResetRestoresThem()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{}")!;
        var fallback = new Locations();
        Assert.Equal(fallback.Resolve(), new SettingsIndexedLocationProvider(settings, fallback).Resolve());
        Assert.False(SettingsIndexedLocationProvider.UsesMft(settings));
        settings.IndexedFolders = [@"C:\Custom"];
        settings.IndexingMethod = IndexScanMethod.Mft;
        settings.ResetToDefaults();
        Assert.Null(settings.IndexedFolders);
        Assert.Equal(IndexScanMethod.SelectedFolders, settings.IndexingMethod);
        Assert.Equal(new IndexBuildOptions(), settings.IndexBuild);
    }

    [Fact]
    public void WorkerAndSortSettingsRoundTripAndReset()
    {
        var options = new IndexBuildOptions(IndexScanWorkerMode.Manual, 12, IndexSortMethod.Merge, false, 8);
        var settings = new AppSettings { IndexBuild = options };
        var copy = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(options, copy.IndexBuild);
        Assert.True(copy.IndexBuild.IsValid);
        Assert.False((options with { ScanWorkers = 17 }).IsValid);
        Assert.False((options with { SortWorkers = 0 }).IsValid);
        copy.ResetToDefaults();
        Assert.Equal(new IndexBuildOptions(), copy.IndexBuild);
    }

    [Fact]
    public void MethodChangesDoNotChangeSelectedRoots()
    {
        var settings = new AppSettings { IndexedFolders = [@"C:\Custom\", @"c:\custom", @"D:\"] };
        var provider = new SettingsIndexedLocationProvider(settings, new Locations());
        var before = provider.Resolve().RootPaths;
        settings.IndexingMethod = IndexScanMethod.Mft;
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;
        Assert.Equal(before, new SettingsIndexedLocationProvider(roundTrip, new Locations()).Resolve().RootPaths);
        Assert.Equal(new[] { @"C:\Custom", @"D:\" }, before);
        Assert.True(SettingsIndexedLocationProvider.UsesMft(roundTrip));
        Assert.False(SettingsIndexedLocationProvider.UsesMft(roundTrip, "directory"));
        Assert.True(SettingsIndexedLocationProvider.UsesMft(new AppSettings(), "MFT"));
    }

    private sealed class Locations : IIndexedLocationProvider
    {
        private readonly IndexLocations _locations = new(@"C:\Desktop", [@"C:\Desktop", @"C:\Documents"]);
        public IndexLocations Resolve() => _locations;
    }
}
