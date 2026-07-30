using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Search;

public sealed class SearchDiagnosticsServiceTests
{
    [Fact]
    public void Inspect_TokenizesAndReturnsIndexSamples()
    {
        var requestedTokens = new List<string>();
        var service = new SearchDiagnosticsService(
            new BasicTokenizer(),
            (token, _) => {
                requestedTokens.Add(token);
                return new IndexTokenMatches(2, [$"{token}.txt"]);
            });

        var diagnostics = service.Inspect("Rapor 2026");

        Assert.Equal(["rapor", "2026"], requestedTokens);
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("rapor", diagnostics[0].Token);
        Assert.Equal(2, diagnostics[0].MatchCount);
        Assert.Equal(["rapor.txt"], diagnostics[0].SampleNames);
    }
}
