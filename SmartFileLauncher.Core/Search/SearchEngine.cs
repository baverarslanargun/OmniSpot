using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search;

public class SearchEngine
{
    private readonly Func<CancellationToken, SearchSnapshot>? _snapshotProvider;
    private readonly Func<CancellationToken, ISearchStateReader>? _searchStateProvider;
    private readonly ITokenizer _tokenizer;
    private readonly IScoringStrategy _scoring;

    public SearchEngine(InvertedIndex invertedIndex, ITokenizer tokenizer, IScoringStrategy scoring)
    {
        ArgumentNullException.ThrowIfNull(invertedIndex);
        _snapshotProvider = cancellationToken =>
            SearchSnapshot.Create(invertedIndex, cancellationToken: cancellationToken);
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
    }

    public SearchEngine(
        Func<CancellationToken, SearchSnapshot> snapshotProvider,
        ITokenizer tokenizer,
        IScoringStrategy scoring)
    {
        _snapshotProvider = snapshotProvider ??
            throw new ArgumentNullException(nameof(snapshotProvider));
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
    }

    public SearchEngine(
        Func<CancellationToken, ISearchStateReader> searchStateProvider,
        ITokenizer tokenizer,
        IScoringStrategy scoring)
    {
        _searchStateProvider = searchStateProvider ??
            throw new ArgumentNullException(nameof(searchStateProvider));
        _tokenizer = tokenizer ?? throw new ArgumentNullException(nameof(tokenizer));
        _scoring = scoring ?? throw new ArgumentNullException(nameof(scoring));
    }

    private const int TrailingPrefixMinimumLength = 2;
    private const double TrailingPrefixWeight = 0.7;

    public IReadOnlyList<SearchResult> Search(
        string query,
        int maxResults = 50,
        CancellationToken cancellationToken = default) =>
        Search(query, maxResults, ResultView.SearchDefault, cancellationToken);

    public IReadOnlyList<SearchResult> Search(
        string query,
        int maxResults,
        ResultView view,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        cancellationToken.ThrowIfCancellationRequested();
        var tokens = _tokenizer.Tokenize(query).ToArray();
        cancellationToken.ThrowIfCancellationRequested();
        if (tokens.Length == 0 || maxResults <= 0)
        {
            return Array.Empty<SearchResult>();
        }

        if (_searchStateProvider != null)
        {
            return Search(_searchStateProvider(cancellationToken).ForQuery(), query, tokens, maxResults, view, cancellationToken);
        }

        return Search(
            _snapshotProvider!(cancellationToken).InvertedIndex,
            query,
            tokens,
            maxResults,
            view,
            cancellationToken);
    }

    private IReadOnlyList<SearchResult> Search(
        InvertedIndexSnapshot invertedIndex,
        string query,
        IReadOnlyList<string> tokens,
        int maxResults,
        ResultView view,
        CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var filter = view.Filter;
        var nodeMatches = new Dictionary<string, (FileSystemNode node, HashSet<string> matchedTokens)>();

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var node in invertedIndex.Get(token))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!nodeMatches.ContainsKey(node.FullPath))
                {
                    nodeMatches[node.FullPath] = (node, new HashSet<string>());
                }
                nodeMatches[node.FullPath].matchedTokens.Add(token);
            }
        }

        var pq = new PriorityQueue<SearchResult, SearchResult>(SortedResultOrder.For(view.Sort));
        foreach (var (_, candidate) in nodeMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var node = candidate.node;
            if (!filter.Matches(
                    node.Name,
                    node.IsDirectory,
                    node.Metadata?.SizeBytes,
                    node.Metadata?.LastWriteTime,
                    now))
            {
                continue;
            }

            var matchedTokens = candidate.matchedTokens;
            var score = CalculateScore(
                node.Name,
                node.Metadata?.OpenCount ?? 0,
                query,
                tokens,
                matchedTokens,
                cancellationToken);

            var result = new SearchResult
            {
                Name = node.Name,
                FullPath = node.FullPath,
                Score = score,
                IsDirectory = node.IsDirectory,
                SizeBytes = node.Metadata?.SizeBytes,
                LastWriteTime = node.Metadata?.LastWriteTime
            };
            pq.Enqueue(result, result);
        }

        return DequeueResults(pq, maxResults, cancellationToken);
    }

    private IReadOnlyList<SearchResult> Search(
        ISearchStateReader state,
        string query,
        IReadOnlyList<string> tokens,
        int maxResults,
        ResultView view,
        CancellationToken cancellationToken)
    {
        var now = DateTime.Now;
        var filter = view.Filter;
        var itemMatches = new Dictionary<string, (SearchItem item, HashSet<string> matchedTokens)>(
            StringComparer.OrdinalIgnoreCase);
        var prefixOnly = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var token in tokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            foreach (var item in state.Get(token))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!itemMatches.ContainsKey(item.FullPath))
                {
                    itemMatches[item.FullPath] = (item, new HashSet<string>());
                }
                itemMatches[item.FullPath].matchedTokens.Add(token);
                prefixOnly.Remove(item.FullPath);
            }
        }

        var trailing = tokens[^1];
        if (trailing.Length >= TrailingPrefixMinimumLength)
        {
            foreach (var item in state.GetPartial(trailing, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (itemMatches.TryGetValue(item.FullPath, out var existing))
                {
                    if (existing.matchedTokens.Add(trailing) && existing.matchedTokens.Count == 1)
                    {
                        prefixOnly.Add(item.FullPath);
                    }
                    continue;
                }

                itemMatches[item.FullPath] = (item, new HashSet<string> { trailing });
                prefixOnly.Add(item.FullPath);
            }
        }

        var pq = new PriorityQueue<SearchResult, SearchResult>(SortedResultOrder.For(view.Sort));
        foreach (var (_, candidate) in itemMatches)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = candidate.item;
            if (!filter.Matches(
                    item.Name,
                    item.IsDirectory,
                    item.SizeBytes,
                    item.LastWriteTime,
                    now))
            {
                continue;
            }

            var score = CalculateScore(
                item.Name,
                item.OpenCount,
                query,
                tokens,
                candidate.matchedTokens,
                cancellationToken);

            if (prefixOnly.Contains(item.FullPath))
            {
                score *= TrailingPrefixWeight;
            }

            var result = new SearchResult
            {
                Name = item.Name,
                FullPath = item.FullPath,
                Score = score,
                IsDirectory = item.IsDirectory,
                SizeBytes = item.SizeBytes,
                LastWriteTime = item.LastWriteTime
            };
            pq.Enqueue(result, result);
        }

        return DequeueResults(pq, maxResults, cancellationToken);
    }

    private double CalculateScore(
        string name,
        int openCount,
        string query,
        IReadOnlyList<string> tokens,
        HashSet<string> matchedTokens,
        CancellationToken cancellationToken)
    {
        var score = matchedTokens.Count * 50d;
        if (matchedTokens.Count == tokens.Count)
        {
            score += 100;
        }

        if (string.Equals(name, query, StringComparison.OrdinalIgnoreCase))
        {
            score += 200;
        }

        var fileTokens = _tokenizer.Tokenize(name).ToHashSet();
        cancellationToken.ThrowIfCancellationRequested();
        foreach (var token in matchedTokens)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (fileTokens.Contains(token))
            {
                score += 25;
            }
        }

        return score + (openCount * 2);
    }

    private static IReadOnlyList<SearchResult> DequeueResults(
        PriorityQueue<SearchResult, SearchResult> pq,
        int maxResults,
        CancellationToken cancellationToken)
    {
        var results = new List<SearchResult>(Math.Min(maxResults, pq.Count));
        while (pq.Count > 0 && results.Count < maxResults)
        {
            cancellationToken.ThrowIfCancellationRequested();
            results.Add(pq.Dequeue());
        }

        return results;
    }
}
