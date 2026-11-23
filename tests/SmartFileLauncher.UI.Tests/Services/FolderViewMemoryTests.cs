using System.IO;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class FolderViewMemoryTests : IDisposable
{
    private static readonly ResultView Images =
        ResultView.FolderDefault with { Filter = new ItemFilter(Categories: FileFilterCategory.Image) };

    private static readonly ResultView NewestFirst =
        ResultView.FolderDefault with { Sort = new ItemSort(SortField.Modified) };

    private static readonly ResultView ImagesNewestFirst =
        NewestFirst with { Filter = Images.Filter };

    private static readonly ResultView LargestFirst =
        ResultView.FolderDefault with { Sort = new ItemSort(SortField.Size) };

    private readonly string _directory =
        Path.Combine(Path.GetTempPath(), "omnispot-folder-views-" + Guid.NewGuid().ToString("N"));

    private string StorePath => Path.Combine(_directory, "folder-views.json");

    public void Dispose()
    {
        if (Directory.Exists(_directory))
        {
            Directory.Delete(_directory, recursive: true);
        }
    }

    private FolderViewMemory NewSession(int capacity = FolderViewMemory.DefaultSortCapacity) =>
        new(new FolderSortStore(StorePath), capacity);

    [Fact]
    public void SortSurvivesANewSession()
    {
        NewSession().Set(@"C:\Is", NewestFirst);

        var next = NewSession();

        Assert.Equal(NewestFirst, next.Get(@"C:\Is"));
        Assert.Equal(ResultView.FolderDefault, next.Get(@"C:\Baska"));
    }

    [Fact]
    public void FilterStaysWithinTheSessionWhileSortIsKept()
    {
        var session = NewSession();
        session.Set(@"C:\Is", ImagesNewestFirst);

        Assert.Equal(ImagesNewestFirst, session.Get(@"C:\Is"));
        Assert.Equal(NewestFirst, NewSession().Get(@"C:\Is"));
    }

    [Fact]
    public void FilterOnlyChangeWritesNothing()
    {
        var session = NewSession();

        session.Set(@"C:\Is", Images);

        Assert.Equal(Images, session.Get(@"C:\Is"));
        Assert.False(File.Exists(StorePath));
    }

    [Fact]
    public void ReturningToTheDefaultSortForgetsTheFolderNextSession()
    {
        var session = NewSession();
        session.Set(@"C:\Is", NewestFirst);

        session.Set(@"C:\Is", Images);

        Assert.Equal(Images, session.Get(@"C:\Is"));
        Assert.Equal(0, session.RememberedSortCount);
        Assert.Equal(ResultView.FolderDefault, NewSession().Get(@"C:\Is"));
    }

    [Fact]
    public void HomeViewSortSurvivesANewSession()
    {
        NewSession().Set(null, LargestFirst);

        Assert.Equal(LargestFirst, NewSession().Get(null));
    }

    [Fact]
    public void SavedFolderIsFoundRegardlessOfCaseAndTrailingSeparator()
    {
        NewSession().Set(@"C:\Is\", NewestFirst);

        var next = NewSession();

        Assert.Equal(NewestFirst, next.Get(@"c:\is"));
        Assert.Equal(1, next.RememberedSortCount);
    }

    [Fact]
    public void LeastRecentlyChangedFolderIsDroppedPastCapacity()
    {
        var session = NewSession(capacity: 2);
        session.Set(@"C:\A", NewestFirst);
        session.Set(@"C:\B", NewestFirst);
        session.Set(@"C:\A", LargestFirst);

        session.Set(@"C:\C", NewestFirst);

        var next = NewSession(capacity: 2);
        Assert.Equal(LargestFirst, next.Get(@"C:\A"));
        Assert.Equal(ResultView.FolderDefault, next.Get(@"C:\B"));
        Assert.Equal(NewestFirst, next.Get(@"C:\C"));
        Assert.Equal(NewestFirst, session.Get(@"C:\B"));
    }

    [Fact]
    public void UnknownFolderUsesTheFolderDefault()
    {
        var memory = new FolderViewMemory();

        Assert.Equal(ResultView.FolderDefault, memory.Get(@"C:\Is"));
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void EachFolderRemembersItsOwnFilterAndSort()
    {
        var memory = new FolderViewMemory();

        memory.Set(@"C:\Is", Images);
        memory.Set(@"C:\Is\alt", NewestFirst);

        Assert.Equal(Images, memory.Get(@"C:\Is"));
        Assert.Equal(NewestFirst, memory.Get(@"C:\Is\alt"));
        Assert.Equal(ResultView.FolderDefault, memory.Get(@"C:\Baska"));
    }

    [Theory]
    [InlineData(@"C:\Is", @"C:\Is\")]
    [InlineData(@"C:\Is", @"c:\is")]
    public void TrailingSeparatorAndCaseDoNotCreateASecondEntry(string stored, string lookup)
    {
        var memory = new FolderViewMemory();

        memory.Set(stored, Images);

        Assert.Equal(Images, memory.Get(lookup));
        Assert.Equal(1, memory.Count);
    }

    [Fact]
    public void RootViewUsesItsOwnSlot()
    {
        var memory = new FolderViewMemory();

        memory.Set(null, Images);

        Assert.Equal(Images, memory.Get(null));
        Assert.Equal(Images, memory.Get("   "));
        Assert.Equal(ResultView.FolderDefault, memory.Get(@"C:\Is"));
    }

    [Fact]
    public void ReturningToTheDefaultViewDropsTheEntry()
    {
        var memory = new FolderViewMemory();
        memory.Set(@"C:\Is", NewestFirst);

        memory.Set(@"C:\Is", ResultView.FolderDefault);

        Assert.Equal(ResultView.FolderDefault, memory.Get(@"C:\Is"));
        Assert.Equal(0, memory.Count);
    }

    [Fact]
    public void SortOnlyChangeIsRemembered()
    {
        var memory = new FolderViewMemory();

        memory.Set(@"C:\Is", NewestFirst);

        Assert.Equal(SortField.Modified, memory.Get(@"C:\Is").Sort.Field);
        Assert.False(memory.Get(@"C:\Is").Filter.IsActive);
        Assert.Equal(1, memory.Count);
    }
}
