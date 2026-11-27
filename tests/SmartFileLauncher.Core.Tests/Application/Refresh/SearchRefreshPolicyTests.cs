using SmartFileLauncher.Core.Application.Refresh;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Refresh;

public sealed class SearchRefreshPolicyTests
{
    [Theory]
    [InlineData("baver.tr", "cache.tmp", false)]
    [InlineData("baver.tr", "BAVER.TR-old.zip", true)]
    [InlineData("yeni", "eski.txt", false)]
    [InlineData("çağ", "ÇAĞRI.txt", true)]
    [InlineData("çağrı", "çağrı.txt", true)]
    [InlineData("çağrı", "cagri.txt", true)]
    [InlineData("cagri", "ÇAĞRI.txt", true)]
    [InlineData("iki kelime", "iki kelime.txt", true)]
    [InlineData("iki kelime", "iki-kelime.txt", true)]
    [InlineData("iki kelime", "kelime.txt", false)]
    [InlineData("çıkmış sorular", "cikmis_sorular.pdf", true)]
    [InlineData("çıkmış sorular", "sorular.txt", false)]
    [InlineData("cikmis_sorular", "Çıkmış Sorular.pdf", true)]
    [InlineData(".pdf", "pdf-rehberi.txt", false)]
    public void NameSearchRefreshesOnlyForNamesItWouldFind(string query, string name, bool expected)
    {
        Assert.Equal(expected, SearchRefreshPolicy.ShouldRefresh(new() { AffectedNames = [name] },
            new(query, false, true), literalNamesOnly: true));
    }

    [Fact]
    public void BothNamesOfARenameAreConsidered()
    {
        var change = new FileChangeEvent { AffectedNames = ["eski.pdf", "yeni.pdf"] };
        Assert.True(SearchRefreshPolicy.ShouldRefresh(change, new("eski", false, true), true));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(change, new("yeni", false, true), true));
    }

    [Fact]
    public void AiDirectoryUnknownScopeAndTimeDependentFiltersStayConservative()
    {
        var request = new SearchRequest("fatura", false, true);
        var unrelated = new FileChangeEvent { AffectedNames = ["cache.tmp"] };
        Assert.True(SearchRefreshPolicy.ShouldRefresh(unrelated, request with { NaturalLanguageMode = true }, true));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(unrelated, request, literalNamesOnly: false));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(new() { IsDirectory = true, AffectedNames = ["folder"] }, request, true));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(new(), request, true));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(unrelated,
            request with { Filter = new ItemFilter(Date: FilterDateRange.LastWeek) }, true));
        Assert.False(SearchRefreshPolicy.ShouldRefresh(unrelated, null, true));
        Assert.False(SearchRefreshPolicy.ShouldRefresh(unrelated, request with { Query = "" }, true));
    }

    [Theory]
    [InlineData("cache.tmp", false)]
    [InlineData("notlar.TXT", false)]
    [InlineData("rapor.pdf", true)]
    [InlineData("RAPOR.PDF", true)]
    public void AiRequestWithTypeFilterRefreshesOnlyForAllowedExtensions(string name, bool expected)
    {
        var query = AiQuery(hardExtensions: ["pdf"], anchors: ["fatura"]);
        Assert.Equal(expected, SearchRefreshPolicy.ShouldRefresh(new() { AffectedNames = ["log.txt", name] },
            AiRequest, true, AiOutcome(query)));
    }

    [Theory]
    [InlineData("tr-kapak.png", true)]
    [InlineData("İSTRAYE.png", true)]
    [InlineData("kapak.png", false)]
    [InlineData("tr.txt", false)]
    public void ShortAiTermsMatchChangedNamesLikePartialIndexMatches(string name, bool expected)
    {
        var query = AiQuery(hardExtensions: ["png"], context: ["tr"], original: ["tr"]);
        Assert.Equal(expected, SearchRefreshPolicy.ShouldRefresh(new() { AffectedNames = [name] },
            AiRequest, true, AiOutcome(query)));
    }

    [Fact]
    public void AiRequestsThatCanExpandFoldersStayConservativeForAllowedNames()
    {
        var change = new FileChangeEvent { AffectedNames = ["ilgisiz.png"] };
        Assert.True(SearchRefreshPolicy.ShouldRefresh(change, AiRequest, true,
            AiOutcome(AiQuery(hardExtensions: ["png"], anchors: ["baver"]))));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(change, AiRequest, true,
            AiOutcome(AiQuery(hardExtensions: ["png"], original: ["baver"]))));
        var hinted = AiQuery(hardExtensions: ["png"], context: ["tr"]);
        hinted.FolderHints.Add(new FolderHint { Name = "baver tr", Weight = 1 });
        Assert.True(SearchRefreshPolicy.ShouldRefresh(change, AiRequest, true, AiOutcome(hinted)));
        var filterOnly = AiQuery(hardExtensions: ["png"]);
        filterOnly.FilterOnlyMode = true;
        Assert.True(SearchRefreshPolicy.ShouldRefresh(change, AiRequest, true, AiOutcome(filterOnly)));
        Assert.False(SearchRefreshPolicy.ShouldRefresh(new() { AffectedNames = ["ilgisiz.txt"] }, AiRequest, true,
            AiOutcome(filterOnly)));
    }

    [Fact]
    public void AiRequestsWithoutAResolvedTypeOrFolderTargetStayConservative()
    {
        var change = new FileChangeEvent { AffectedNames = ["ilgisiz.txt"] };
        var folders = AiQuery(hardExtensions: ["pdf"], anchors: ["fatura"]);
        folders.TargetType = new TargetType { Folder = 1, File = 0 };
        Assert.True(SearchRefreshPolicy.ShouldRefresh(change, AiRequest, true, AiOutcome(folders)));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(change, AiRequest, true,
            AiOutcome(new StructuredQuery { Keywords = ["fatura"] })));
        Assert.False(SearchRefreshPolicy.ShouldRefresh(change, AiRequest, true,
            AiOutcome(new StructuredQuery { Keywords = ["fatura"], HardExtensions = ["pdf"] })));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(change, AiRequest, true, null));
    }

    [Fact]
    public void AiRequiredNameMustAppearInTheChangedName()
    {
        var query = AiQuery(hardExtensions: ["jpg"], context: ["tr"]);
        query.RequiredName = "resim";
        Assert.False(SearchRefreshPolicy.ShouldRefresh(new() { AffectedNames = ["tr-kapak.jpg"] }, AiRequest, true,
            AiOutcome(query)));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(new() { AffectedNames = ["tr-Resim.jpg"] }, AiRequest, true,
            AiOutcome(query)));
    }

    [Fact]
    public void AiRequestAnsweredByLiteralSearchUsesTheLiteralNameMatcher()
    {
        var literal = new SearchOutcome([], SearchExecutionMode.Standard, false, null, null, null, null);
        var request = new SearchRequest("baver", true, true);
        Assert.False(SearchRefreshPolicy.ShouldRefresh(new() { AffectedNames = ["cache.tmp"] }, request, true, literal));
        Assert.True(SearchRefreshPolicy.ShouldRefresh(new() { AffectedNames = ["baver.tr"] }, request, true, literal));
    }

    private static readonly SearchRequest AiRequest = new("baver tr resim dosyaları", true, true);

    private static SearchOutcome AiOutcome(StructuredQuery query) =>
        new([], SearchExecutionMode.Advanced, false, null, null, null, query);

    private static StructuredQuery AiQuery(string[]? hardExtensions = null, string[]? anchors = null,
        string[]? context = null, string[]? original = null) => new()
    {
        OriginalText = "sorgu",
        OriginalTerms = original ?? [],
        HardExtensions = [.. hardExtensions ?? []],
        SearchTerms =
        [
            .. (anchors ?? []).Select(text => new SearchTerm { Text = text, Role = SearchTermRole.Anchor, Weight = 1 }),
            .. (context ?? []).Select(text => new SearchTerm { Text = text, Role = SearchTermRole.Context, Weight = 0.35 })
        ],
        TargetType = new TargetType { File = 1, Folder = 0 }
    };
}
