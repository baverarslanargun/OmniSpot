using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Models;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Search;

public sealed class SearchApplicationServiceTests
{
    [Fact]
    public async Task StandardModeUsesOnlyStandardSearch()
    {
        var standardCalls = 0;
        var advancedCalls = 0;
        var parserCalls = 0;
        var expected = Result("standard.txt");
        var service = CreateService(
            standardSearch: (query, maxResults, cancellationToken) =>
            {
                standardCalls++;
                Assert.Equal("standard", query);
                Assert.Equal(12, maxResults);
                return new[] { expected };
            },
            advancedSearch: (query, maxResults, cancellationToken) =>
            {
                advancedCalls++;
                return Array.Empty<SearchResult>();
            },
            onlineParser: (query, cancellationToken) =>
            {
                parserCalls++;
                return Task.FromResult(new StructuredQuery());
            });

        var outcome = await service.SearchAsync(
            new SearchRequest("standard", false, true, 12));

        Assert.Equal(SearchExecutionMode.Standard, outcome.Mode);
        Assert.Same(expected, Assert.Single(outcome.Results));
        Assert.Equal(1, standardCalls);
        Assert.Equal(0, advancedCalls);
        Assert.Equal(0, parserCalls);
    }

    [Fact]
    public async Task OfflineNaturalLanguageSearchUsesStandardFallbackWithoutParsing()
    {
        var parserCalls = 0;
        var service = CreateService(
            standardSearch: (query, maxResults, cancellationToken) =>
                new[] { Result("offline.txt") },
            onlineParser: (query, cancellationToken) =>
            {
                parserCalls++;
                return Task.FromResult(new StructuredQuery());
            });

        var outcome = await service.SearchAsync(
            new SearchRequest("offline", true, false));

        Assert.Equal(SearchExecutionMode.OfflineFallback, outcome.Mode);
        Assert.True(outcome.UsedFallback);
        Assert.Equal("İnternet bağlantısı yok", outcome.FallbackReason);
        Assert.Equal(0, parserCalls);
        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task OnlineNaturalLanguageSearchPassesStructuredQueryToAdvancedSearch()
    {
        var structuredQuery = new StructuredQuery
        {
            Keywords = new List<string> { "invoice" },
            OpenAction = new OpenAction
            {
                ShouldOpen = true,
                OpenMode = "single_best"
            }
        };
        var expected = Result("invoice.pdf");
        var service = CreateService(
            advancedSearch: (query, maxResults, cancellationToken) =>
            {
                Assert.Same(structuredQuery, query);
                Assert.Equal(100, maxResults);
                return new[] { expected };
            },
            onlineParser: (query, cancellationToken) =>
                Task.FromResult(structuredQuery));

        var outcome = await service.SearchAsync(
            new SearchRequest("find invoice", true, true));

        Assert.Equal(SearchExecutionMode.Advanced, outcome.Mode);
        Assert.Same(structuredQuery, outcome.StructuredQuery);
        Assert.Equal(expected.FullPath, outcome.AutoOpenPath);
    }

    [Fact]
    public async Task OnlineParserFailureUsesRuleBasedAdvancedSearch()
    {
        var fallbackQuery = new StructuredQuery
        {
            Keywords = new List<string> { "fallback" }
        };
        var ruleBasedCalls = 0;
        var service = CreateService(
            advancedSearch: (query, maxResults, cancellationToken) =>
            {
                Assert.Same(fallbackQuery, query);
                return new[] { Result("fallback.txt") };
            },
            onlineParser: (query, cancellationToken) =>
                throw new InvalidOperationException("Groq kullanılamıyor"),
            ruleBasedParser: query =>
            {
                ruleBasedCalls++;
                return fallbackQuery;
            });

        var outcome = await service.SearchAsync(
            new SearchRequest("fallback", true, true));

        Assert.Equal(SearchExecutionMode.RuleBasedFallback, outcome.Mode);
        Assert.True(outcome.UsedFallback);
        Assert.Equal("Groq kullanılamıyor", outcome.FallbackReason);
        Assert.Equal(1, ruleBasedCalls);
        Assert.Single(outcome.Results);
    }

    [Fact]
    public async Task ParserCancellationIsNotConvertedToFallback()
    {
        var ruleBasedCalls = 0;
        var service = CreateService(
            onlineParser: (query, cancellationToken) =>
                throw new OperationCanceledException(cancellationToken),
            ruleBasedParser: query =>
            {
                ruleBasedCalls++;
                return new StructuredQuery();
            });

        await Assert.ThrowsAsync<OperationCanceledException>(() =>
            service.SearchAsync(new SearchRequest("cancel", true, true)));
        Assert.Equal(0, ruleBasedCalls);
    }

    [Fact]
    public async Task ParserFallbackAndWarningArePreserved()
    {
        var structuredQuery = new StructuredQuery
        {
            UsedFallback = true,
            FallbackReason = "intent fallback",
            WarningMessage = "keyword warning"
        };
        var service = CreateService(
            onlineParser: (query, cancellationToken) =>
                Task.FromResult(structuredQuery));

        var outcome = await service.SearchAsync(
            new SearchRequest("warning", true, true));

        Assert.True(outcome.UsedFallback);
        Assert.Equal("intent fallback", outcome.FallbackReason);
        Assert.Equal("keyword warning", outcome.WarningMessage);
    }

    [Theory]
    [InlineData(false, "single_best")]
    [InlineData(true, "show_list")]
    public async Task NonOpeningActionsDoNotReturnAutoOpenPath(
        bool shouldOpen,
        string openMode)
    {
        var structuredQuery = new StructuredQuery
        {
            OpenAction = new OpenAction
            {
                ShouldOpen = shouldOpen,
                OpenMode = openMode
            }
        };
        var service = CreateService(
            advancedSearch: (query, maxResults, cancellationToken) =>
                new[] { Result("result.txt") },
            onlineParser: (query, cancellationToken) =>
                Task.FromResult(structuredQuery));

        var outcome = await service.SearchAsync(
            new SearchRequest("result", true, true));

        Assert.Null(outcome.AutoOpenPath);
    }

    [Fact]
    public async Task AmbiguousOpeningActionDoesNotReturnAutoOpenPath()
    {
        var structuredQuery = new StructuredQuery
        {
            OpenAction = new OpenAction
            {
                ShouldOpen = true,
                OpenMode = "single_best"
            }
        };
        var service = CreateService(
            advancedSearch: (query, maxResults, cancellationToken) =>
                new[]
                {
                    Result("first.txt", 120),
                    Result("second.txt", 100)
                },
            onlineParser: (query, cancellationToken) =>
                Task.FromResult(structuredQuery));

        var outcome = await service.SearchAsync(
            new SearchRequest("first dosyasını aç", true, true));

        Assert.Null(outcome.AutoOpenPath);
    }

    [Fact]
    public async Task ClearOpeningActionReturnsAutoOpenPath()
    {
        var structuredQuery = new StructuredQuery
        {
            OpenAction = new OpenAction
            {
                ShouldOpen = true,
                OpenMode = "single_best"
            }
        };
        var service = CreateService(
            advancedSearch: (query, maxResults, cancellationToken) =>
                new[]
                {
                    Result("first.txt", 180),
                    Result("second.txt", 100)
                },
            onlineParser: (query, cancellationToken) =>
                Task.FromResult(structuredQuery));

        var outcome = await service.SearchAsync(
            new SearchRequest("first dosyasını aç", true, true));

        Assert.Equal(@"C:\Workspace\first.txt", outcome.AutoOpenPath);
    }

    private static SearchApplicationService CreateService(
        Func<string, int, CancellationToken, IReadOnlyList<SearchResult>>? standardSearch = null,
        Func<StructuredQuery, int, CancellationToken, IReadOnlyList<SearchResult>>? advancedSearch = null,
        Func<string, CancellationToken, Task<StructuredQuery>>? onlineParser = null,
        Func<string, StructuredQuery>? ruleBasedParser = null) =>
        new(
            standardSearch ?? ((query, maxResults, cancellationToken) =>
                Array.Empty<SearchResult>()),
            advancedSearch ?? ((query, maxResults, cancellationToken) =>
                Array.Empty<SearchResult>()),
            onlineParser ?? ((query, cancellationToken) =>
                Task.FromResult(new StructuredQuery())),
            ruleBasedParser ?? (query => new StructuredQuery()));

    private static SearchResult Result(string name, double score = 100) =>
        new()
        {
            Name = name,
            FullPath = $@"C:\Workspace\{name}",
            Score = score
        };
}
