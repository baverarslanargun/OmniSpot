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
    [InlineData("çağrı", "cagri.txt", false)]
    [InlineData("iki kelime", "iki kelime.txt", true)]
    [InlineData("iki kelime", "iki-kelime.txt", false)]
    public void LiteralSearchIgnoresUnrelatedNamesWithoutChangingItsMatchingRules(string query, string name, bool expected)
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
}
