using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Models;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class FileIndexSuggestionTests
{
    private const ulong Root = 5;

    private static FileIndex Build(params (string Name, bool Folder)[] names)
    {
        var entries = new List<IndexEntry> { Entry(Root, Root, @"C:\", FileAttributes.Directory) };
        for (var i = 0; i < names.Length; i++)
            entries.Add(Entry((ulong)(16 + i), Root, names[i].Name, names[i].Folder ? FileAttributes.Directory : FileAttributes.Normal));
        var index = FileIndex.Build(@"C:\", Root, entries);
        index.PrepareWords(default);
        return index;
    }

    private static FileIndex Sample() => Build(
        ("fatura_mart.pdf", false), ("fatura_nisan.pdf", false), ("faturalar", true), ("Çıkmış Sorular.pdf", false),
        ("readme.md", false), ("rapor.docx", false), ("notlar.txt", false), ("screenshot.png", false));

    private static string? Suggest(FileIndex index, string query, ResultView? view = null) =>
        index.SuggestQuery(query, view ?? ResultView.SearchDefault, null, default)?.Query;

    [Theory]
    [InlineData("faturs", "fatura")]
    [InlineData("FATURS", "fatura")]
    [InlineData("fatrua", "fatura")]
    [InlineData("raopr", "rapor")]
    [InlineData("screnshot", "screenshot")]
    [InlineData("readmee", "readme")]
    [InlineData("cikmş sorular", "cikmis sorular")]
    [InlineData("sorular_cikmş", "sorular cikmis")]
    [InlineData("rapr docx", "rapor docx")]
    public void AMisspelledWordIsReplacedByTheClosestWordThatFindsSomething(string query, string expected)
    {
        using var index = Sample();
        var suggestion = Suggest(index, query);

        Assert.Equal(expected, suggestion);
        Assert.Empty(index.Search(query, 10, ResultView.SearchDefault));
        Assert.NotEmpty(index.Search(suggestion!, 10, ResultView.SearchDefault));
    }

    [Theory]
    [InlineData("fatura")]
    [InlineData("fatura rapor")]
    [InlineData("fat")]
    [InlineData("zqxjv")]
    [InlineData("fatura zqxjv")]
    [InlineData("rapor2024x")]
    [InlineData("   ")]
    [InlineData("\"faturs\"")]
    [InlineData("faturs !mart")]
    public void NoSuggestionWhenNothingIsMisspelledOrNothingIsClose(string query)
    {
        using var index = Sample();
        Assert.Null(Suggest(index, query));
    }

    [Fact]
    public void TheSuggestionMustFindSomethingUnderTheActiveFilterAndScope()
    {
        using var index = Sample();
        var large = ResultView.SearchDefault with { Filter = new ItemFilter(Kind: FilterItemKind.FilesOnly, Size: FilterSizeRange.Large) };
        var folders = ResultView.SearchDefault with { Filter = new ItemFilter(Kind: FilterItemKind.FoldersOnly) };

        Assert.Equal("fatura", Suggest(index, "faturs"));
        Assert.Equal("fatura", Suggest(index, "faturs", folders));
        Assert.Null(Suggest(index, "faturs", large));
        Assert.Null(Suggest(index, "faturs", ResultView.SearchDefault with { Scope = @"C:\faturalar" }));
    }

    [Fact]
    public void TheMoreFruitfulOfEquallyCloseWordsWins()
    {
        using var index = Build(("kart.txt", false), ("kara_1.txt", false), ("kara_2.txt", false), ("kara_3.txt", false));
        Assert.Equal("kara", Suggest(index, "karx"));
    }

    [Fact]
    public void ACloserWordBeatsAMoreCommonOneAndWholeWordsBeatWordFragments()
    {
        using var index = Build(("readme.md", false), ("reader_1.txt", false), ("reader_2.txt", false), ("reader_3.txt", false),
            ("microsoft_a.txt", false), ("microsoft_b.txt", false), ("microso.txt", false));

        Assert.Equal("readme", Suggest(index, "readmee"));
        Assert.Equal("microsoft", Suggest(index, "microsfot"));
    }

    [Fact]
    public void TheReaderPicksTheVolumeWhoseSuggestionFindsMore()
    {
        using var left = Build(("kart.txt", false));
        using var right = Build(("kara_1.txt", false), ("kara_2.txt", false), ("kara_3.txt", false));
        using var other = Build(("rapor.docx", false));
        using var reader = new FileIndexReader([(left, null), (right, null), (other, null)]);
        using var unrelated = new FileIndexReader([(other, null)]);

        Assert.Equal("kara", reader.SuggestLiteralName("karx", ResultView.SearchDefault, default));
        Assert.Null(unrelated.SuggestLiteralName("karx", ResultView.SearchDefault, default));
    }

    [Fact]
    public async Task TheServiceAsksOnlyForEmptyStandardSearches()
    {
        var calls = new List<string>();
        var service = new SearchApplicationService(
            (_, _, _, _) => [], (_, _, _, _) => [], (_, _, _) => Task.FromResult(new StructuredQuery()), _ => new StructuredQuery(),
            standardSuggestion: (query, _, _) => { calls.Add(query); return "fatura"; });
        var request = new SearchRequest("faturs", false, true);
        var empty = new SearchOutcome([], SearchExecutionMode.Standard, false, null, null, null, null);
        var found = empty with { Results = [new SearchResult { Name = "x", FullPath = @"C:\x" }] };
        var ai = empty with { StructuredQuery = new StructuredQuery() };

        Assert.Equal("fatura", await service.SuggestAsync(request, empty));
        Assert.Null(await service.SuggestAsync(request, found));
        Assert.Null(await service.SuggestAsync(request, ai));
        Assert.Equal(new[] { "faturs" }, calls);
    }

    private static IndexEntry Entry(ulong id, ulong parent, string name, FileAttributes attributes) =>
        new(id, parent, name, attributes, 20, DateTime.UnixEpoch.Ticks);
}
