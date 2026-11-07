using SmartFileLauncher.Core.Application.Files;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Files;

public sealed class FolderPathTrailTests
{
    [Fact]
    public void BuildStartsAtTheContainingIndexedRoot()
    {
        var segments = FolderPathTrail.Build(
            @"C:\Kullanıcılar\baver\Belgeler\Muhasebe",
            [@"D:\Arşiv", @"C:\Kullanıcılar\baver"]);

        Assert.Equal(
            [
                new FolderPathSegment("baver", @"C:\Kullanıcılar\baver"),
                new FolderPathSegment("Belgeler", @"C:\Kullanıcılar\baver\Belgeler"),
                new FolderPathSegment("Muhasebe", @"C:\Kullanıcılar\baver\Belgeler\Muhasebe")
            ],
            segments);
    }

    [Fact]
    public void BuildPrefersTheDeepestMatchingRootAndNamesDrives()
    {
        Assert.Equal(
            [new FolderPathSegment("C:", @"C:\"), new FolderPathSegment("Users", @"C:\Users")],
            FolderPathTrail.Build(@"C:\Users\", [@"C:\"]));

        var nested = FolderPathTrail.Build(@"C:\Root\Inner\Leaf", [@"C:\", @"c:\root\inner"]);
        Assert.Equal(["Inner", "Leaf"], nested.Select(segment => segment.Name));
    }

    [Fact]
    public void BuildDoesNotTreatASiblingWithTheSamePrefixAsTheRoot()
    {
        var segments = FolderPathTrail.Build(@"C:\Root2\Child", [@"C:\Root"]);

        Assert.Equal(["C:", "Root2", "Child"], segments.Select(segment => segment.Name));
    }

    [Fact]
    public void BuildReturnsOnlyTheRootWhenItIsTheCurrentFolder()
    {
        var segment = Assert.Single(FolderPathTrail.Build(@"C:\Root\", [@"C:\Root"]));

        Assert.Equal(new FolderPathSegment("Root", @"C:\Root"), segment);
    }

    [Fact]
    public void FitKeepsShortPathsWhole()
    {
        var segments = FolderPathTrail.Build(@"C:\Users\baver", [@"C:\"]);

        var layout = FolderPathTrail.Fit(segments, 72);

        Assert.Equal(segments, layout.Leading);
        Assert.Empty(layout.Hidden);
        Assert.Empty(layout.Trailing);
    }

    [Fact]
    public void FitHidesMiddleFoldersAndKeepsTheRootAndTheCurrentFolder()
    {
        var segments = FolderPathTrail.Build(
            @"C:\Kullanıcılar\baver\Belgeler\Muhasebe\2026\Faturalar\Elektrik",
            [@"C:\"]);

        var layout = FolderPathTrail.Fit(segments, 40);

        Assert.Equal(["C:"], layout.Leading.Select(segment => segment.Name));
        Assert.Equal("Elektrik", layout.Trailing[^1].Name);
        Assert.NotEmpty(layout.Hidden);
        Assert.Equal(
            segments.Select(segment => segment.Path),
            layout.Leading.Concat(layout.Hidden).Concat(layout.Trailing).Select(segment => segment.Path));
    }

    [Fact]
    public void FitAlwaysShowsTheCurrentFolderEvenWhenItIsLong()
    {
        var segments = FolderPathTrail.Build(
            @"C:\A\B\" + new string('x', 80),
            [@"C:\"]);

        var layout = FolderPathTrail.Fit(segments, 10);

        Assert.Equal(["C:"], layout.Leading.Select(segment => segment.Name));
        Assert.Equal(["A", "B"], layout.Hidden.Select(segment => segment.Name));
        Assert.Single(layout.Trailing);
    }
}
