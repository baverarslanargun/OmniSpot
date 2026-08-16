using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class SearchResultOrderingTests
{
    private const string Root = @"C:\Data";

    private static readonly string[] TiedNames =
    [
        "rapor-c.txt",
        "rapor-a.txt",
        "rapor-d.txt",
        "rapor-b.txt"
    ];

    private static readonly string[] ExpectedTiedOrder =
    [
        @"C:\Data\rapor-a.txt",
        @"C:\Data\rapor-b.txt",
        @"C:\Data\rapor-c.txt",
        @"C:\Data\rapor-d.txt"
    ];

    [Fact]
    public void StandardEngineOverSearchStateOrdersTiesByName()
    {
        var results = SearchViaState(TiedNames, "rapor", maxResults: 50);

        Assert.Equal(ExpectedTiedOrder, results);
    }

    [Fact]
    public void StandardEngineOverInvertedIndexOrdersTiesByName()
    {
        var results = SearchViaInvertedIndex(TiedNames, "rapor", maxResults: 50);

        Assert.Equal(ExpectedTiedOrder, results);
    }

    [Fact]
    public void AdvancedEngineOrdersTiesByName()
    {
        var results = SearchViaAdvanced(TiedNames, "rapor", maxResults: 50);

        Assert.Equal(ExpectedTiedOrder, results);
    }

    [Fact]
    public void ReversedInputOrderProducesTheSameResult()
    {
        var forward = SearchViaState(TiedNames, "rapor", maxResults: 50);
        var reversed = SearchViaState(TiedNames.Reverse().ToArray(), "rapor", maxResults: 50);

        Assert.Equal(forward, reversed);
        Assert.Equal(ExpectedTiedOrder, reversed);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(3)]
    public void TruncationKeepsTheSameMembersAndOrder(int maxResults)
    {
        var expected = ExpectedTiedOrder.Take(maxResults).ToArray();

        Assert.Equal(expected, SearchViaState(TiedNames, "rapor", maxResults));
        Assert.Equal(expected, SearchViaInvertedIndex(TiedNames, "rapor", maxResults));
        Assert.Equal(expected, SearchViaAdvanced(TiedNames, "rapor", maxResults));
        Assert.Equal(
            expected,
            SearchViaState(TiedNames.Reverse().ToArray(), "rapor", maxResults));
    }

    [Fact]
    public void OpenCountBeatsAlphabeticalTieBreak()
    {
        var opened = Node("zzz-rapor.txt", openCount: 1);
        var never = Node("aaa-rapor.txt");
        var state = SearchState.Create([never, opened], new BasicTokenizer());
        var engine = new SearchEngine(_ => state, new BasicTokenizer(), new BasicScoringStrategy());

        var results = engine.Search("rapor", maxResults: 10);

        Assert.Equal(opened.FullPath, results[0].FullPath);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void TinyButRealScoreDifferenceIsNotRoundedAway()
    {
        var higher = Result("zzz.txt", @"C:\Data\zzz.txt", 175.0000001d);
        var lower = Result("aaa.txt", @"C:\Data\aaa.txt", 175d);

        var ordered = Sort([lower, higher]);

        Assert.Equal(higher.FullPath, ordered[0].FullPath);
    }

    [Fact]
    public void OrderIsTotalEvenForCaseOnlyPathDifferences()
    {
        var upper = Result("rapor.txt", @"C:\Data\RAPOR.TXT", 175d);
        var lower = Result("rapor.txt", @"C:\Data\rapor.txt", 175d);

        Assert.NotEqual(0, SearchResultOrder.Instance.Compare(upper, lower));
        Assert.Equal(
            -SearchResultOrder.Instance.Compare(upper, lower),
            SearchResultOrder.Instance.Compare(lower, upper));
    }

    private static SearchResult Result(string name, string fullPath, double score) =>
        new() { Name = name, FullPath = fullPath, Score = score };

    private static SearchResult[] Sort(SearchResult[] results)
    {
        var sorted = results.ToArray();
        Array.Sort(sorted, SearchResultOrder.Instance);
        return sorted;
    }

    private static string[] SearchViaState(string[] names, string query, int maxResults)
    {
        var tokenizer = new BasicTokenizer();
        var state = SearchState.Create(names.Select(name => Node(name)).ToArray(), tokenizer);
        return new SearchEngine(_ => state, tokenizer, new BasicScoringStrategy())
            .Search(query, maxResults)
            .Select(result => result.FullPath)
            .ToArray();
    }

    private static string[] SearchViaInvertedIndex(string[] names, string query, int maxResults)
    {
        var tokenizer = new BasicTokenizer();
        var index = new InvertedIndex();
        foreach (var name in names)
        {
            var node = Node(name);
            foreach (var token in tokenizer.Tokenize(node.Name))
            {
                index.Add(token, node);
            }
        }

        return new SearchEngine(index, tokenizer, new BasicScoringStrategy())
            .Search(query, maxResults)
            .Select(result => result.FullPath)
            .ToArray();
    }

    private static string[] SearchViaAdvanced(string[] names, string query, int maxResults)
    {
        var tokenizer = new BasicTokenizer();
        var state = SearchState.Create(names.Select(name => Node(name)).ToArray(), tokenizer);
        return new AdvancedSearchEngine(_ => state, tokenizer, new BasicScoringStrategy())
            .Search(new StructuredQuery { Keywords = [query] }, maxResults)
            .Select(result => result.FullPath)
            .ToArray();
    }

    private static FileSystemNode Node(string name, int openCount = 0) =>
        new(name, Root + "\\" + name, false)
        {
            Metadata = new FileMetadata { OpenCount = openCount }
        };
}
