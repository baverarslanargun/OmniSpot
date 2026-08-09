using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Application.Search;

public sealed class SearchDiagnosticsService : ISearchDiagnosticsService
{
    private readonly ITokenizer _tokenizer;
    private readonly Func<CancellationToken, SearchSnapshot> _snapshotProvider;

    public SearchDiagnosticsService(
        ITokenizer tokenizer,
        Func<CancellationToken, SearchSnapshot> snapshotProvider)
    {
        _tokenizer = tokenizer
            ?? throw new ArgumentNullException(nameof(tokenizer));
        _snapshotProvider = snapshotProvider
            ?? throw new ArgumentNullException(nameof(snapshotProvider));
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
        var invertedIndex = _snapshotProvider(cancellationToken).InvertedIndex;
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
