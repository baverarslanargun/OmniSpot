using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Search;

public sealed class SearchDiagnosticsServiceTests
{
    [Fact]
    public void Inspect_TokenizesAndReturnsIndexSamples()
    {
        var index = new InvertedIndex();
        var tokenizer = new BasicTokenizer();
        var report = new FileSystemNode("rapor.txt", @"C:\files\rapor.txt", false);
        var year = new FileSystemNode("2026.txt", @"C:\files\2026.txt", false);
        index.Add("rapor", report);
        index.Add("2026", year);
        var snapshotCalls = 0;
        var service = new SearchDiagnosticsService(
            tokenizer,
            _ =>
            {
                snapshotCalls++;
                return SearchSnapshot.Create(index);
            });

        var diagnostics = service.Inspect("Rapor 2026");

        Assert.Equal(1, snapshotCalls);
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal("rapor", diagnostics[0].Token);
        Assert.Equal(1, diagnostics[0].MatchCount);
        Assert.Equal(["rapor.txt"], diagnostics[0].SampleNames);
    }
    [Fact]
    public void Inspect_UsesOneImmutableSearchState()
    {
        var tokenizer = new BasicTokenizer();
        var report = new FileSystemNode("rapor.txt", @"C:\files\rapor.txt", false);
        var state = SearchState.Create([report], tokenizer);
        var stateCalls = 0;
        var service = new SearchDiagnosticsService(
            tokenizer,
            _ =>
            {
                stateCalls++;
                return state;
            });

        var diagnostics = service.Inspect("Rapor missing");

        Assert.Equal(1, stateCalls);
        Assert.Equal(2, diagnostics.Count);
        Assert.Equal(1, diagnostics[0].MatchCount);
        Assert.Equal(0, diagnostics[1].MatchCount);
    }
}
