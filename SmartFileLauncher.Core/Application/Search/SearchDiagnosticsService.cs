using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Application.Search;

public sealed class SearchDiagnosticsService : ISearchDiagnosticsService
{
    private readonly ITokenizer _tokenizer;
    private readonly Func<CancellationToken, SearchSnapshot>? _snapshotProvider;
    private readonly Func<CancellationToken, SearchState>? _searchStateProvider;

    public SearchDiagnosticsService(
        ITokenizer tokenizer,
        Func<CancellationToken, SearchSnapshot> snapshotProvider)
    {
        _tokenizer = tokenizer
            ?? throw new ArgumentNullException(nameof(tokenizer));
        _snapshotProvider = snapshotProvider
            ?? throw new ArgumentNullException(nameof(snapshotProvider));
    }

    public SearchDiagnosticsService(
        ITokenizer tokenizer,
        Func<CancellationToken, SearchState> searchStateProvider)
    {
        _tokenizer = tokenizer
            ?? throw new ArgumentNullException(nameof(tokenizer));
        _searchStateProvider = searchStateProvider
            ?? throw new ArgumentNullException(nameof(searchStateProvider));
    }

    public IReadOnlyList<string> Tokenize(string query)
    {
        ArgumentNullException.ThrowIfNull(query);
        return _tokenizer.Tokenize(query).ToArray();
    }

    public IReadOnlyList<SearchTokenDiagnostics> Inspect(
        string query,
        CancellationToken cancellationToken = default)
    {
        var diagnostics = new List<SearchTokenDiagnostics>();
        if (_searchStateProvider != null)
        {
            var searchState = _searchStateProvider(cancellationToken);
            foreach (var token in Tokenize(query))
            {
                cancellationToken.ThrowIfCancellationRequested();
                var matches = searchState.Get(token);
                diagnostics.Add(new SearchTokenDiagnostics(
                    token,
                    matches.Count,
                    matches.Take(3).Select(item => item.Name).ToArray()));
            }

            return diagnostics;
        }

        var invertedIndex = _snapshotProvider!(cancellationToken).InvertedIndex;
        foreach (var token in Tokenize(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = invertedIndex.Get(token);
            diagnostics.Add(new SearchTokenDiagnostics(
                token,
                matches.Count,
                matches.Take(3).Select(node => node.Name).ToArray()));
        }

        return diagnostics;
    }
}
