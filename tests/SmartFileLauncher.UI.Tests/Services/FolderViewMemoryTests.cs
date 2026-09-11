using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class FolderViewMemoryTests
{
    private static readonly ResultView Images =
        ResultView.FolderDefault with { Filter = new ItemFilter(Categories: FileFilterCategory.Image) };

    private static readonly ResultView NewestFirst =
        ResultView.FolderDefault with { Sort = new ItemSort(SortField.Modified) };

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
