using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class SearchFilterTests
{
    [Fact]
    public void StandardSearchKeepsOnlyTheSelectedCategory()
    {
        var engine = CreateStateEngine(
            File("rapor.txt", @"C:\İş\rapor.txt"),
            File("rapor.png", @"C:\İş\rapor.png"),
            File("rapor.mp4", @"C:\İş\rapor.mp4"));

        var results = engine.Search(
            "rapor",
            50,
            new ResultView(new ItemFilter(Categories: FileFilterCategory.Image)));

        Assert.Equal(@"C:\İş\rapor.png", Assert.Single(results).FullPath);
    }

    [Fact]
    public void StandardSearchFiltersTheWholeMatchSetBeforeTrimmingToMaxResults()
    {
        var nodes = new List<FileSystemNode>();
        for (var index = 0; index < 5; index++)
        {
            nodes.Add(File($"rapor-{index}.txt", $@"C:\İş\rapor-{index}.txt", openCount: 10));
        }

        nodes.Add(File("rapor arşiv.png", @"C:\İş\rapor arşiv.png"));
        var engine = CreateStateEngine(nodes.ToArray());

        var unfiltered = engine.Search("rapor", 2);
        var filtered = engine.Search(
            "rapor",
            2,
            new ResultView(new ItemFilter(Categories: FileFilterCategory.Image)));

        Assert.DoesNotContain(unfiltered, result => result.FullPath.EndsWith(".png"));
        Assert.Equal(@"C:\İş\rapor arşiv.png", Assert.Single(filtered).FullPath);
    }

    [Fact]
    public void StandardSearchKeepsFoldersUnderDateAndSizeFilters()
    {
        var engine = CreateStateEngine(
            Directory("rapor", @"C:\İş\rapor"),
            File("rapor.txt", @"C:\İş\rapor.txt", size: 10, modified: DateTime.Now.AddYears(-3)));

        var results = engine.Search(
            "rapor",
            50,
            new ResultView(new ItemFilter(Date: FilterDateRange.Today, Size: FilterSizeRange.Large)));

        var result = Assert.Single(results);
        Assert.Equal(@"C:\İş\rapor", result.FullPath);
        Assert.True(result.IsDirectory);
    }

    [Fact]
    public void StandardSearchFilesOnlyFilterRemovesFolders()
    {
        var engine = CreateStateEngine(
            Directory("rapor", @"C:\İş\rapor"),
            File("rapor.txt", @"C:\İş\rapor.txt"));

        var results = engine.Search("rapor", 50, new ResultView(new ItemFilter(Kind: FilterItemKind.FilesOnly)));

        Assert.Equal(@"C:\İş\rapor.txt", Assert.Single(results).FullPath);
    }

    [Fact]
    public void StandardSearchOnInvertedIndexAppliesTheFilter()
    {
        var index = new InvertedIndex();
        var tokenizer = new BasicTokenizer();
        var text = File("rapor.txt", @"C:\İş\rapor.txt");
        var image = File("rapor.png", @"C:\İş\rapor.png");
        foreach (var node in new[] { text, image })
        {
            foreach (var token in tokenizer.Tokenize(node.Name))
            {
                index.Add(token, node);
            }
        }

        var engine = new SearchEngine(index, tokenizer, new BasicScoringStrategy());

        var results = engine.Search(
            "rapor",
            50,
            new ResultView(new ItemFilter(Categories: FileFilterCategory.Image)));

        Assert.Equal(@"C:\İş\rapor.png", Assert.Single(results).FullPath);
    }

    [Fact]
    public void AdvancedSearchAppliesTheUserFilterOnTopOfTheStructuredQuery()
    {
        var root = Directory("İş", @"C:\İş");
        var text = File("rapor.txt", @"C:\İş\rapor.txt");
        var image = File("rapor.png", @"C:\İş\rapor.png");
        var engine = CreateAdvancedEngine(root, text, image);
        var query = new StructuredQuery
        {
            SearchTerms = [new() { Text = "rapor", Weight = 1 }],
            TargetType = new() { File = 1, Folder = 0 }
        };

        Assert.Equal(2, engine.Search(query).Count);

        var filtered = engine.Search(
            query,
            100,
            new ResultView(new ItemFilter(Categories: FileFilterCategory.Image)));

        Assert.Equal(@"C:\İş\rapor.png", Assert.Single(filtered).FullPath);
    }

    [Fact]
    public void FilterOnlyFastPathAppliesTheUserFilter()
    {
        var tokenizer = new BasicTokenizer();
        var root = Directory("İş", @"C:\İş");
        var text = File("rapor.txt", @"C:\İş\rapor.txt");
        var image = File("rapor.png", @"C:\İş\rapor.png");
        root.AddChild(text);
        root.AddChild(image);
        var state = CompactSearchState.Create([root, text, image], tokenizer);
        var engine = new AdvancedSearchEngine(
            _ => (ISearchStateReader)state,
            tokenizer,
            new BasicScoringStrategy());
        var query = new StructuredQuery
        {
            FilterOnlyMode = true,
            TargetType = new() { File = 1, Folder = 0 }
        };

        var unfiltered = engine.Search(query);
        var filtered = engine.Search(
            query,
            100,
            new ResultView(new ItemFilter(Categories: FileFilterCategory.Image)));

        Assert.Equal(2, unfiltered.Count);
        Assert.Equal(@"C:\İş\rapor.png", Assert.Single(filtered).FullPath);
    }

    private static SearchEngine CreateStateEngine(params FileSystemNode[] nodes)
    {
        var tokenizer = new BasicTokenizer();
        var state = SearchState.Create(nodes, tokenizer);
        return new SearchEngine(
            _ => (ISearchStateReader)state,
            tokenizer,
            new BasicScoringStrategy());
    }

    private static AdvancedSearchEngine CreateAdvancedEngine(
        FileSystemNode root,
        params FileSystemNode[] nodes)
    {
        var index = new InvertedIndex();
        var tokenizer = new BasicTokenizer();
        foreach (var node in nodes)
        {
            root.AddChild(node);
            foreach (var token in tokenizer.Tokenize(node.Name))
            {
                index.Add(token, node);
            }
        }

        return new AdvancedSearchEngine(
            index,
            tokenizer,
            new BasicScoringStrategy(),
            root);
    }

    private static FileSystemNode File(
        string name,
        string path,
        long? size = null,
        DateTime? modified = null,
        int openCount = 0) =>
        new(name, path, false)
        {
            Metadata = new FileMetadata
            {
                SizeBytes = size ?? 1024,
                LastWriteTime = modified ?? DateTime.Now,
                OpenCount = openCount
            }
        };

    private static FileSystemNode Directory(string name, string path) => new(name, path, true);
}
