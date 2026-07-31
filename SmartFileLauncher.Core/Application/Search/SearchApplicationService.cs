using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Application.Search;

public sealed class SearchApplicationService : ISearchApplicationService
{
    private readonly Func<string, int, CancellationToken, IReadOnlyList<SearchResult>> _standardSearch;
    private readonly Func<StructuredQuery, int, CancellationToken, IReadOnlyList<SearchResult>> _advancedSearch;
    private readonly Func<string, CancellationToken, Task<StructuredQuery>> _onlineIntentParser;
    private readonly Func<string, StructuredQuery> _ruleBasedIntentParser;

    public SearchApplicationService(
        Func<string, int, CancellationToken, IReadOnlyList<SearchResult>> standardSearch,
        Func<StructuredQuery, int, CancellationToken, IReadOnlyList<SearchResult>> advancedSearch,
        Func<string, CancellationToken, Task<StructuredQuery>> onlineIntentParser,
        Func<string, StructuredQuery> ruleBasedIntentParser)
    {
        _standardSearch = standardSearch ?? throw new ArgumentNullException(nameof(standardSearch));
        _advancedSearch = advancedSearch ?? throw new ArgumentNullException(nameof(advancedSearch));
        _onlineIntentParser = onlineIntentParser ?? throw new ArgumentNullException(nameof(onlineIntentParser));
        _ruleBasedIntentParser = ruleBasedIntentParser ?? throw new ArgumentNullException(nameof(ruleBasedIntentParser));
    }

    public async Task<SearchOutcome> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.NaturalLanguageMode)
        {
            var standardResults = await RunStandardSearchAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return new SearchOutcome(
                standardResults,
                SearchExecutionMode.Standard,
                false,
                null,
                null,
                null,
                null);
        }

        if (!request.HasInternetConnection)
        {
            var fallbackResults = await RunStandardSearchAsync(request, cancellationToken)
                .ConfigureAwait(false);
            return new SearchOutcome(
                fallbackResults,
                SearchExecutionMode.OfflineFallback,
                true,
                "İnternet bağlantısı yok",
                null,
                null,
                null);
        }

        var mode = SearchExecutionMode.Advanced;
        StructuredQuery structuredQuery;

        try
        {
            structuredQuery = await _onlineIntentParser(request.Query, cancellationToken)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            mode = SearchExecutionMode.RuleBasedFallback;
            structuredQuery = _ruleBasedIntentParser(request.Query);
            structuredQuery.UsedFallback = true;
            structuredQuery.FallbackReason = ex.Message;
        }

        cancellationToken.ThrowIfCancellationRequested();
        structuredQuery ??= CreateDefaultQuery(request.Query);

        var results = await Task.Run(
                () => _advancedSearch(
                    structuredQuery,
                    request.MaxResults,
                    cancellationToken),
                cancellationToken)
            .ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        var autoOpenPath = ResolveAutoOpenPath(structuredQuery, results);
        var usedFallback = structuredQuery.UsedFallback ||
                           mode == SearchExecutionMode.RuleBasedFallback;

        return new SearchOutcome(
            results,
            mode,
            usedFallback,
            structuredQuery.FallbackReason,
            structuredQuery.WarningMessage,
            autoOpenPath,
            structuredQuery);
    }

    private Task<IReadOnlyList<SearchResult>> RunStandardSearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => _standardSearch(
                request.Query,
                request.MaxResults,
                cancellationToken),
            cancellationToken);

    private static string? ResolveAutoOpenPath(
        StructuredQuery query,
        IReadOnlyList<SearchResult> results)
    {
        if (query.OpenAction?.ShouldOpen != true ||
            query.OpenAction.OpenMode != "single_best" ||
            results.Count == 0)
        {
            return null;
        }

        var best = results[0];
        if (best.Score < 100)
        {
            return null;
        }

        if (results.Count > 1)
        {
            var runnerUp = results[1];
            var requiredMargin = Math.Max(40, Math.Abs(runnerUp.Score) * 0.25);
            if (best.Score - runnerUp.Score < requiredMargin)
            {
                return null;
            }
        }

        return best.FullPath;
    }

    private static StructuredQuery CreateDefaultQuery(string query) =>
        new()
        {
            Intent = "search_files",
            Keywords = new List<string> { query },
            FileTypes = new List<string>(),
            PredictedExtensions = new List<string>(),
            IncludeFolderContents = true,
            UsedFallback = true,
            FallbackReason = "Parser sonucu boş"
        };
}
