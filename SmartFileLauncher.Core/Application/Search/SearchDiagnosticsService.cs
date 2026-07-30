using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Application.Search;

public sealed class SearchDiagnosticsService : ISearchDiagnosticsService
{
    private readonly ITokenizer _tokenizer;
    private readonly Func<string, CancellationToken, IndexTokenMatches>
        _getTokenMatches;

    public SearchDiagnosticsService(
        ITokenizer tokenizer,
        Func<string, CancellationToken, IndexTokenMatches> getTokenMatches)
    {
        _tokenizer = tokenizer
            ?? throw new ArgumentNullException(nameof(tokenizer));
        _getTokenMatches = getTokenMatches
            ?? throw new ArgumentNullException(nameof(getTokenMatches));
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
        foreach (var token in Tokenize(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var matches = _getTokenMatches(token, cancellationToken);
            diagnostics.Add(new SearchTokenDiagnostics(
                token,
                matches.Count,
                matches.SampleNames));
        }

        return diagnostics;
    }
}
