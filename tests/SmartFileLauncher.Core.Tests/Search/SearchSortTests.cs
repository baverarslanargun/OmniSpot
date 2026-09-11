using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class SearchSortTests
{
    private static readonly DateTime Base = new(2026, 9, 11, 12, 0, 0, DateTimeKind.Local);

    [Fact]
    public void RelevanceStaysTheDefaultOrder()
    {
        var engine = CreateEngine(
            File("rapor.txt", modified: Base, size: 10, openCount: 0),
            File("rapor.png", modified: Base.AddDays(-1), size: 900, openCount: 40));

        var results = engine.Search("rapor", 10);

        Assert.Equal("rapor.png", results[0].Name);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void NameSortIsAlphabeticalAndReversible()
    {
        var engine = CreateEngine(
            File("rapor-c.txt", openCount: 30),
            File("rapor-a.txt"),
            File("rapor-b.txt", openCount: 10));

        var ascending = engine.Search("rapor", 10, View(SortField.Name, descending: false));
        var descending = engine.Search("rapor", 10, View(SortField.Name, descending: true));

        Assert.Equal(new[] { "rapor-a.txt", "rapor-b.txt", "rapor-c.txt" }, ascending.Select(r => r.Name));
        Assert.Equal(new[] { "rapor-c.txt", "rapor-b.txt", "rapor-a.txt" }, descending.Select(r => r.Name));
    }

    [Fact]
    public void ModifiedSortPutsTheNewestFirst()
    {
        var engine = CreateEngine(
            File("rapor-eski.txt", modified: Base.AddYears(-2), openCount: 50),
            File("rapor-yeni.txt", modified: Base),
            File("rapor-orta.txt", modified: Base.AddMonths(-6)));

        var results = engine.Search("rapor", 10, View(SortField.Modified, descending: true));

        Assert.Equal(
            new[] { "rapor-yeni.txt", "rapor-orta.txt", "rapor-eski.txt" },
            results.Select(r => r.Name));
    }

    [Fact]
    public void SizeSortPutsTheLargestFirstAndKeepsFoldersLast()
    {
        var engine = CreateEngine(
            File("rapor-kucuk.txt", size: 10),
            File("rapor-buyuk.txt", size: 10_000),
            Directory("rapor", @"C:\Is\rapor"));

        var results = engine.Search("rapor", 10, View(SortField.Size, descending: true));

        Assert.Equal(
            new[] { "rapor-buyuk.txt", "rapor-kucuk.txt", "rapor" },
            results.Select(r => r.Name));
    }

    [Fact]
    public void SizeSortAscendingAlsoKeepsFoldersLast()
    {
        var engine = CreateEngine(
            File("rapor-kucuk.txt", size: 10),
            File("rapor-buyuk.txt", size: 10_000),
            Directory("rapor", @"C:\Is\rapor"));

        var results = engine.Search("rapor", 10, View(SortField.Size, descending: false));

        Assert.Equal(
            new[] { "rapor-kucuk.txt", "rapor-buyuk.txt", "rapor" },
            results.Select(r => r.Name));
    }

    [Fact]
    public void KindSortGroupsByExtension()
    {
        var engine = CreateEngine(
            File("rapor.txt"),
            File("rapor.png"),
            File("rapor.docx"));

        var results = engine.Search("rapor", 10, View(SortField.Kind, descending: false));

        Assert.Equal(
            new[] { "rapor.docx", "rapor.png", "rapor.txt" },
            results.Select(r => r.Name));
    }

    [Fact]
    public void SortRunsOverTheWholeMatchSetBeforeTrimmingToMaxResults()
    {
        var nodes = new List<FileSystemNode>();
        for (var index = 0; index < 5; index++)
        {
            nodes.Add(File($"rapor-{index}.txt", modified: Base.AddYears(-3), openCount: 40));
        }

        nodes.Add(File("rapor arşiv.txt", modified: Base));
        var engine = CreateEngine(nodes.ToArray());

        var byRelevance = engine.Search("rapor", 1);
        var byDate = engine.Search("rapor", 1, View(SortField.Modified, descending: true));

        Assert.NotEqual("rapor arşiv.txt", Assert.Single(byRelevance).Name);
        Assert.Equal("rapor arşiv.txt", Assert.Single(byDate).Name);
    }

    [Fact]
    public void FilterAndSortApplyTogether()
    {
        var engine = CreateEngine(
            File("rapor-yeni.txt", modified: Base),
            File("rapor-a.png", modified: Base.AddDays(-3)),
            File("rapor-b.png", modified: Base.AddDays(-1)));

        var results = engine.Search(
            "rapor",
            10,
            new ResultView(
                new ItemFilter(Categories: FileFilterCategory.Image),
                new ItemSort(SortField.Modified, Descending: true)));

        Assert.Equal(new[] { "rapor-b.png", "rapor-a.png" }, results.Select(r => r.Name));
    }

    [Fact]
    public void ResultsCarryTheMetadataTheSortUses()
    {
        var engine = CreateEngine(File("rapor.txt", modified: Base, size: 4096));

        var result = Assert.Single(engine.Search("rapor", 10));

        Assert.Equal(4096, result.SizeBytes);
        Assert.Equal(Base, result.LastWriteTime);
    }

    [Fact]
    public void AdvancedSearchHonorsTheSort()
    {
        var tokenizer = new BasicTokenizer();
        var root = Directory("Is", @"C:\Is");
        var older = File("rapor-eski.txt", modified: Base.AddYears(-1), openCount: 30);
        var newer = File("rapor-yeni.txt", modified: Base);
        var state = CompactSearchState.Create([root, older, newer], tokenizer);
        var engine = new AdvancedSearchEngine(
            _ => (ISearchStateReader)state,
            tokenizer,
            new BasicScoringStrategy());
        var query = new StructuredQuery
        {
            FilterOnlyMode = true,
            TargetType = new() { File = 1, Folder = 0 }
        };

        var results = engine.Search(
            query,
            10,
            new ResultView(ItemFilter.None, new ItemSort(SortField.Modified, Descending: true)));

        Assert.Equal(
            new[] { "rapor-yeni.txt", "rapor-eski.txt" },
            results.Select(r => r.Name));
    }

    private static ResultView View(SortField field, bool descending) =>
        new(ItemFilter.None, new ItemSort(field, descending));

    private static SearchEngine CreateEngine(params FileSystemNode[] nodes)
    {
        var tokenizer = new BasicTokenizer();
        var state = SearchState.Create(nodes, tokenizer);
        return new SearchEngine(
            _ => (ISearchStateReader)state,
            tokenizer,
            new BasicScoringStrategy());
    }

    private static FileSystemNode File(
        string name,
        long? size = null,
        DateTime? modified = null,
        int openCount = 0) =>
        new(name, $@"C:\Is\{name}", false)
        {
            Metadata = new FileMetadata
            {
                SizeBytes = size ?? 1024,
                LastWriteTime = modified ?? Base,
                OpenCount = openCount
            }
        };

    private static FileSystemNode Directory(string name, string path) => new(name, path, true);
}
