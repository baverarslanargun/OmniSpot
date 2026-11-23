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
        Assert.Equal(IndexVolumeMethod.Folders, SettingsIndexedLocationProvider.MethodFor(settings, @"C:\"));
        settings.IndexedFolders = [@"C:\Custom"];
        settings.IndexingMethod = IndexScanMethod.Mft;
        settings.IndexVolumeMethods = new() { [@"E:\"] = IndexVolumeMethod.Folders };
        settings.ResetToDefaults();
        Assert.Null(settings.IndexedFolders);
        Assert.Null(settings.IndexVolumeMethods);
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
        Assert.Equal(IndexVolumeMethod.Automatic, SettingsIndexedLocationProvider.MethodFor(roundTrip, @"C:\Custom"));
        Assert.Equal(IndexVolumeMethod.Folders, SettingsIndexedLocationProvider.MethodFor(roundTrip, @"C:\", "directory"));
        Assert.Equal(IndexVolumeMethod.Mft, SettingsIndexedLocationProvider.MethodFor(new AppSettings(), @"C:\", "MFT"));
    }

    [Fact]
    public void EachVolumeKeepsItsOwnMethodAcrossARoundTrip()
    {
        var settings = new AppSettings
        {
            IndexingMethod = IndexScanMethod.Mft,
            IndexedFolders = [@"C:\", @"e:\Arşiv", @"\\sunucu\paylaşım\Belgeler"],
            IndexVolumeMethods = new() { [@"e:\"] = IndexVolumeMethod.Folders, [@"C:\"] = IndexVolumeMethod.Mft, [@"\\sunucu\paylaşım"] = IndexVolumeMethod.Folders }
        };
        var roundTrip = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(settings))!;

        Assert.Equal(IndexVolumeMethod.Mft, SettingsIndexedLocationProvider.MethodFor(roundTrip, @"C:\Windows"));
        Assert.Equal(IndexVolumeMethod.Folders, SettingsIndexedLocationProvider.MethodFor(roundTrip, @"E:\Arşiv\2024"));
        Assert.Equal(IndexVolumeMethod.Folders, SettingsIndexedLocationProvider.MethodFor(roundTrip, @"\\SUNUCU\Paylaşım\Belgeler"));
        Assert.Equal(IndexVolumeMethod.Automatic, SettingsIndexedLocationProvider.MethodFor(roundTrip, @"D:\"));
        Assert.Equal(@"E:\", SettingsIndexedLocationProvider.VolumeRoot(@"e:\Arşiv"));
        Assert.Equal(@"\\sunucu\paylaşım\", SettingsIndexedLocationProvider.VolumeRoot(@"\\sunucu\paylaşım\Belgeler"));
        roundTrip.IndexVolumeMethods![@"E:\"] = (IndexVolumeMethod)42;
        roundTrip.IndexVolumeMethods.Remove(@"e:\");
        Assert.Equal(IndexVolumeMethod.Automatic, SettingsIndexedLocationProvider.MethodFor(roundTrip, @"E:\"));
    }

    [Fact]
    public void NetworkLocationsNeverOfferMft()
    {
        Assert.Equal(IndexVolumeKind.Other, IndexVolumeProbe.KindOf(@"\\sunucu\paylaşım\"));
        Assert.False(IndexVolumeProbe.AllowsMft(@"\\sunucu\paylaşım\"));
        Assert.Equal(IndexVolumeKind.Ntfs, IndexVolumeProbe.KindOf(Path.GetPathRoot(Environment.SystemDirectory)!));
    }

    private sealed class Locations : IIndexedLocationProvider
    {
        private readonly IndexLocations _locations = new(@"C:\Desktop", [@"C:\Desktop", @"C:\Documents"]);
        public IndexLocations Resolve() => _locations;
    }
}
