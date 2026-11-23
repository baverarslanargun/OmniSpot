using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Application.Search;

public sealed class SearchApplicationService : ISearchApplicationService
{
    private readonly Func<string, int, ResultView, CancellationToken, IReadOnlyList<SearchResult>> _standardSearch;
    private readonly Func<StructuredQuery, int, ResultView, CancellationToken, IReadOnlyList<SearchResult>> _advancedSearch;
    private readonly Func<string, string?, CancellationToken, Task<StructuredQuery>> _onlineIntentParser;
    private readonly Func<string, StructuredQuery> _ruleBasedIntentParser;
    private readonly Func<string, int, int, ResultView, CancellationToken, IReadOnlyList<SearchResult>>? _standardPage;
    private readonly Func<string, ResultView, CancellationToken, int?>? _standardCount;
    private readonly Func<LiteralRanking>? _literalRanking;

    public SearchApplicationService(
        Func<string, int, ResultView, CancellationToken, IReadOnlyList<SearchResult>> standardSearch,
        Func<StructuredQuery, int, ResultView, CancellationToken, IReadOnlyList<SearchResult>> advancedSearch,
        Func<string, CancellationToken, Task<StructuredQuery>> onlineIntentParser,
        Func<string, StructuredQuery> ruleBasedIntentParser,
        Func<string, int, int, ResultView, CancellationToken, IReadOnlyList<SearchResult>>? standardPage = null,
        Func<string, ResultView, CancellationToken, int?>? standardCount = null)
        : this(standardSearch, advancedSearch,
            onlineIntentParser == null ? throw new ArgumentNullException(nameof(onlineIntentParser)) :
                (query, _, cancellationToken) => onlineIntentParser(query, cancellationToken),
            ruleBasedIntentParser, standardPage, standardCount)
    {
    }

    public SearchApplicationService(
        Func<string, int, ResultView, CancellationToken, IReadOnlyList<SearchResult>> standardSearch,
        Func<StructuredQuery, int, ResultView, CancellationToken, IReadOnlyList<SearchResult>> advancedSearch,
        Func<string, string?, CancellationToken, Task<StructuredQuery>> onlineIntentParser,
        Func<string, StructuredQuery> ruleBasedIntentParser,
        Func<string, int, int, ResultView, CancellationToken, IReadOnlyList<SearchResult>>? standardPage = null,
        Func<string, ResultView, CancellationToken, int?>? standardCount = null,
        Func<LiteralRanking>? literalRanking = null)
    {
        _standardSearch = standardSearch ?? throw new ArgumentNullException(nameof(standardSearch));
        _advancedSearch = advancedSearch ?? throw new ArgumentNullException(nameof(advancedSearch));
        _onlineIntentParser = onlineIntentParser ?? throw new ArgumentNullException(nameof(onlineIntentParser));
        _ruleBasedIntentParser = ruleBasedIntentParser ?? throw new ArgumentNullException(nameof(ruleBasedIntentParser));
        _standardPage = standardPage;
        _standardCount = standardCount;
        _literalRanking = literalRanking;
    }

    public Task<IReadOnlyList<SearchResult>> LoadPageAsync(
        SearchRequest request,
        SearchOutcome outcome,
        int offset,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outcome);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        cancellationToken.ThrowIfCancellationRequested();
        if (!outcome.IsPageable || limit <= 0)
            return Task.FromResult<IReadOnlyList<SearchResult>>(Array.Empty<SearchResult>());
        return Task.Run(() =>
        {
            var view = request.View with { LiteralRanking = outcome.LiteralRanking };
            if (_standardPage != null)
                return _standardPage(request.Query, offset, limit, view, cancellationToken);
            var window = _standardSearch(request.Query, (int)Math.Min(int.MaxValue, (long)offset + limit),
                view, cancellationToken);
            return window.Count <= offset ? Array.Empty<SearchResult>() : (IReadOnlyList<SearchResult>)window.Skip(offset).ToArray();
        }, cancellationToken);
    }

    public Task<int?> CountAsync(
        SearchRequest request,
        SearchOutcome outcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(outcome);
        cancellationToken.ThrowIfCancellationRequested();
        if (!outcome.IsPageable || _standardCount == null) return Task.FromResult<int?>(null);
        return Task.Run(() => _standardCount(request.Query, request.View, cancellationToken), cancellationToken);
    }

    public async Task<SearchOutcome> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        if (!request.NaturalLanguageMode)
        {
            return await StandardOutcomeAsync(request, SearchExecutionMode.Standard, null, cancellationToken)
                .ConfigureAwait(false);
        }

        if (!request.HasInternetConnection)
        {
            return await StandardOutcomeAsync(request, SearchExecutionMode.OfflineFallback,
                "İnternet bağlantısı yok", cancellationToken).ConfigureAwait(false);
        }

        var mode = SearchExecutionMode.Advanced;
        StructuredQuery structuredQuery;

        try
        {
            structuredQuery = await _onlineIntentParser(request.Query, request.ReasoningEffort, cancellationToken)
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

        IReadOnlyList<SearchResult> results;
        try
        {
            results = await Task.Run(
                    () => _advancedSearch(
                        structuredQuery,
                        request.MaxResults,
                        request.View,
                        cancellationToken),
                    cancellationToken)
                .ConfigureAwait(false);
        }
        catch (TokenIndexNotReadyException failure)
        {
            return await StandardOutcomeAsync(request, SearchExecutionMode.Standard, failure.Message, cancellationToken)
                .ConfigureAwait(false);
        }

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

    public async Task<SearchOutcome> RefreshAsync(
        SearchRequest request,
        SearchOutcome previousOutcome,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(previousOutcome);
        cancellationToken.ThrowIfCancellationRequested();

        var results = previousOutcome.StructuredQuery is { } structuredQuery
            ? await Task.Run(
                () => _advancedSearch(structuredQuery, request.MaxResults, request.View, cancellationToken),
                cancellationToken).ConfigureAwait(false)
            : await RunStandardSearchAsync(request, previousOutcome.LiteralRanking, cancellationToken).ConfigureAwait(false);

        cancellationToken.ThrowIfCancellationRequested();
        return previousOutcome with { Results = results, AutoOpenPath = null };
    }

    private async Task<SearchOutcome> StandardOutcomeAsync(
        SearchRequest request, SearchExecutionMode mode, string? fallbackReason, CancellationToken cancellationToken)
    {
        var ranking = request.View.Sort.IsRelevance ? _literalRanking?.Invoke() : null;
        var results = await RunStandardSearchAsync(request, ranking, cancellationToken).ConfigureAwait(false);
        return new SearchOutcome(results, mode, fallbackReason != null, fallbackReason, null, null, null)
        {
            LiteralRanking = ranking
        };
    }

    private Task<IReadOnlyList<SearchResult>> RunStandardSearchAsync(
        SearchRequest request,
        LiteralRanking? ranking,
        CancellationToken cancellationToken) =>
        Task.Run(
            () => _standardSearch(
                request.Query,
                request.MaxResults,
                request.View with { LiteralRanking = ranking },
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
