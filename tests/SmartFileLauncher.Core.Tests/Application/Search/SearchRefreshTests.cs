using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Search;

public sealed class SearchRefreshTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RefreshReusesResolvedIntentAndViewWithoutParsingOrAutoOpening(bool fallback)
    {
        var parsed = 0;
        var searched = 0;
        var structured = new StructuredQuery
        {
            Keywords = ["fatura"],
            UsedFallback = fallback,
            FallbackReason = fallback ? "intent fallback" : null,
            WarningMessage = "keyword warning",
            OpenAction = new OpenAction { ShouldOpen = true, OpenMode = "single_best" }
        };
        var request = new SearchRequest("faturayı aç", true, true, 17,
            Sort: new ItemSort(SortField.Modified, true));
        var service = new SearchApplicationService(
            (_, _, _, _) => throw new InvalidOperationException("Standart arama çağrılmamalı"),
            (query, limit, view, _) =>
            {
                Assert.Same(structured, query);
                Assert.Equal(request.MaxResults, limit);
                Assert.Equal(request.View, view);
                return [Result($"fatura-{++searched}.pdf")];
            },
            (_, _) => { parsed++; return Task.FromResult(structured); },
            _ => throw new InvalidOperationException("Parser çağrılmamalı"));

        var initial = await service.SearchAsync(request);
        Assert.NotNull(initial.AutoOpenPath);
        var refreshed = await service.RefreshAsync(request, initial);
        var again = await service.RefreshAsync(request, refreshed);

        Assert.Equal(1, parsed);
        Assert.Equal(3, searched);
        Assert.Equal("fatura-3.pdf", Assert.Single(again.Results).Name);
        Assert.Equal(initial.Mode, again.Mode);
        Assert.Equal(initial.UsedFallback, again.UsedFallback);
        Assert.Equal(initial.FallbackReason, again.FallbackReason);
        Assert.Equal(initial.WarningMessage, again.WarningMessage);
        Assert.Same(structured, again.StructuredQuery);
        Assert.True(structured.OpenAction.ShouldOpen);
        Assert.Null(refreshed.AutoOpenPath);
        Assert.Null(again.AutoOpenPath);
    }

    [Theory]
    [InlineData(SearchExecutionMode.Standard)]
    [InlineData(SearchExecutionMode.OfflineFallback)]
    public async Task StandardRefreshKeepsExecutionModeAndNeverInvokesAParser(SearchExecutionMode mode)
    {
        var request = new SearchRequest("rapor", mode == SearchExecutionMode.OfflineFallback, true, 9);
        var previous = new SearchOutcome([], mode, mode == SearchExecutionMode.OfflineFallback,
            "offline", null, null, null);
        var service = new SearchApplicationService(
            (query, limit, _, _) =>
            {
                Assert.Equal(request.Query, query);
                Assert.Equal(9, limit);
                return [Result("rapor.pdf")];
            },
            (_, _, _, _) => throw new InvalidOperationException(),
            (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException());

        var refreshed = await service.RefreshAsync(request, previous);

        Assert.Equal(previous with { Results = refreshed.Results }, refreshed);
        Assert.Equal("rapor.pdf", Assert.Single(refreshed.Results).Name);
    }

    [Fact]
    public async Task RuleBasedRefreshPreservesFallbackWithoutRerunningEitherParser()
    {
        var previous = new SearchOutcome([], SearchExecutionMode.RuleBasedFallback, true,
            "provider unavailable", "warning", "old.pdf", new StructuredQuery { Keywords = ["rapor"] });
        var service = new SearchApplicationService(
            (_, _, _, _) => throw new InvalidOperationException(),
            (_, _, _, _) => [Result("rapor.pdf")],
            (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException());

        var refreshed = await service.RefreshAsync(new("rapor", true, true), previous);

        Assert.Equal(previous with { Results = refreshed.Results, AutoOpenPath = null }, refreshed);
    }

    [Fact]
    public async Task RefreshCancellationReachesTheLocalSearchWithoutFallback()
    {
        using var cancellation = new CancellationTokenSource();
        var previous = new SearchOutcome([], SearchExecutionMode.Advanced, false, null, null, null, new());
        var service = new SearchApplicationService(
            (_, _, _, _) => throw new InvalidOperationException(),
            (_, _, _, token) =>
            {
                Assert.Equal(cancellation.Token, token);
                cancellation.Cancel();
                token.ThrowIfCancellationRequested();
                return [];
            },
            (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException());

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            service.RefreshAsync(new("rapor", true, true), previous, cancellation.Token));
    }

    [Fact]
    public async Task AnUnavailableTokenIndexDoesNotReplaceAnAiRefreshWithStandardSearch()
    {
        var previous = new SearchOutcome([Result("fatura.pdf")], SearchExecutionMode.Advanced,
            false, null, null, null, new());
        var service = new SearchApplicationService(
            (_, _, _, _) => throw new InvalidOperationException("Standart arama çağrılmamalı"),
            (_, _, _, _) => throw new TokenIndexNotReadyException(),
            (_, _) => throw new InvalidOperationException(),
            _ => throw new InvalidOperationException());

        await Assert.ThrowsAsync<TokenIndexNotReadyException>(() =>
            service.RefreshAsync(new("fatura", true, true), previous));
    }

    private static SearchResult Result(string name) => new()
    {
        Name = name,
        FullPath = Path.Combine(@"C:\Belgeler", name),
        Score = 200
    };
}
