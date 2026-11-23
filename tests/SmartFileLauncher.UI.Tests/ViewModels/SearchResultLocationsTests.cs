using SmartFileLauncher.UI.ViewModels;
using Xunit;

namespace SmartFileLauncher.UI.Tests.ViewModels;

public sealed class SearchResultLocationsTests
{
    private const string GameJam = @"C:\Fixtures\Projects\GameJam\";

    [Fact]
    public void OnlyResultsSharingANameGetTheirFolderAsLocation()
    {
        var results = new[]
        {
            Result("Sample", @"C:\Users\sample\GameJam2025\Sample"),
            Result("sample", @"C:\Users\sample"),
            Result("sample", GameJam + @"GameJam2025\TeamA\sample"),
            Result("sample", GameJam + @"GameJam2025\TeamB\client\sample"),
            Result("sample-avatar-splash.svg", @"C:\Site\assets\sample-avatar-splash.svg")
        };

        SearchResultLocations.Apply(results);

        Assert.Equal(["GameJam2025", "Users", "TeamA", "client", null], results.Select(result => result.Location));
    }

    [Fact]
    public void SameNamedFoldersAreTellApartByAddingParentFoldersOnlyWhereNeeded()
    {
        var results = new[]
        {
            Result("sample", GameJam + @"GameJam\yedek\sample"),
            Result("sample", GameJam + @"GameJam2025\yedek\sample"),
            Result("sample", GameJam + @"GameJam2025\Ortak\sample"),
            Result("sample", @"C:\sample")
        };

        SearchResultLocations.Apply(results);

        Assert.Equal([@"GameJam\yedek", @"GameJam2025\yedek", "Ortak", @"C:\"], results.Select(result => result.Location));
    }

    [Fact]
    public void ALaterPageThatCreatesACollisionAddsTheLocationAndARemovedTwinClearsIt()
    {
        var first = Result("rapor.pdf", @"C:\Belgeler\rapor.pdf");
        var results = new List<SearchResultViewModel> { first };
        SearchResultLocations.Apply(results);
        Assert.Null(first.Location);

        var twin = Result("RAPOR.pdf", @"C:\Arsiv\RAPOR.pdf");
        results.Add(twin);
        SearchResultLocations.Apply(results);
        Assert.Equal("Belgeler", first.Location);
        Assert.Equal("Arsiv", twin.Location);

        results.Remove(twin);
        SearchResultLocations.Apply(results);
        Assert.Null(first.Location);
    }

    private static SearchResultViewModel Result(string name, string path) => new() { Name = name, FullPath = path };
}
