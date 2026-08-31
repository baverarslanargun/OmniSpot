using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class SearchStateChildEdgeTests
{
    private const string Root = @"C:\Data";
    private const string Folder = @"C:\Data\Klasor";
    private const string ChildFile = @"C:\Data\Klasor\dosya.txt";

    [Fact]
    public void ChildAddedBeforeItsParentStillBecomesADescendant()
    {
        var (folder, child) = BuildFolderWithChild();

        var state = SearchState.Empty
            .WithUpserts([child], new BasicTokenizer())
            .WithUpserts([folder], new BasicTokenizer());

        Assert.Equal([ChildFile], DescendantPaths(state, Folder));
    }

    [Fact]
    public void ParentAddedBeforeItsChildStillBecomesADescendant()
    {
        var (folder, child) = BuildFolderWithChild();

        var state = SearchState.Empty
            .WithUpserts([folder], new BasicTokenizer())
            .WithUpserts([child], new BasicTokenizer());

        Assert.Equal([ChildFile], DescendantPaths(state, Folder));
    }

    [Fact]
    public void CreateKeepsTheChildEdgeWhenTheParentIsOutsideTheNodeSet()
    {
        var (_, child) = BuildFolderWithChild();

        var state = SearchState.Create([child], new BasicTokenizer());

        Assert.Equal([ChildFile], DescendantPaths(state, Folder));
    }

    [Fact]
    public void RemovingAChildDropsItsEdge()
    {
        var (folder, child) = BuildFolderWithChild();
        var state = SearchState.Empty
            .WithUpserts([child], new BasicTokenizer())
            .WithUpserts([folder], new BasicTokenizer());

        Assert.Equal([ChildFile], DescendantPaths(state, Folder));
        Assert.Empty(DescendantPaths(state.WithoutPathAndDescendants(ChildFile), Folder));
    }

    [Fact]
    public void DirectoryReplacedByAFileLosesItsDescendants()
    {
        var (folder, child) = BuildFolderWithChild();
        var state = SearchState.Empty.WithUpserts([folder, child], new BasicTokenizer());
        Assert.Equal([ChildFile], DescendantPaths(state, Folder));

        var replaced = state.WithUpserts(
            [new FileSystemNode("Klasor", Folder, false)],
            new BasicTokenizer());

        Assert.Empty(DescendantPaths(replaced, Folder));
        Assert.Empty(replaced.Get("dosya"));
    }

    [Fact]
    public void ChildEdgeResolvesParentPathCaseInsensitively()
    {
        var upperFolder = new FileSystemNode("KLASOR", @"C:\Data\KLASOR", true);
        var child = new FileSystemNode("dosya.txt", @"C:\Data\KLASOR\dosya.txt", false);
        upperFolder.AddChild(child);

        var state = SearchState.Empty.WithUpserts([child], new BasicTokenizer());

        Assert.Equal(
            [@"C:\Data\KLASOR\dosya.txt"],
            DescendantPaths(state, @"C:\data\klasor"));
    }

    private static string[] DescendantPaths(SearchState state, string parentPath) =>
        state.GetDescendants(ParentItem(parentPath))
            .Select(item => item.FullPath)
            .ToArray();

    private static SearchItem ParentItem(string parentPath) =>
        new("Klasor", parentPath, true, null, null, null, 0, Root);

    private static (FileSystemNode Folder, FileSystemNode Child) BuildFolderWithChild()
    {
        var folder = new FileSystemNode("Klasor", Folder, true);
        var child = new FileSystemNode("dosya.txt", ChildFile, false);
        folder.AddChild(child);
        return (folder, child);
    }
}
