namespace SmartFileLauncher.Core.Application.Search;

public sealed record SearchTokenDiagnostics(
    string Token,
    int MatchCount,
    IReadOnlyList<string> SampleNames);

public interface ISearchDiagnosticsService
{
    IReadOnlyList<string> Tokenize(string query);

    IReadOnlyList<SearchTokenDiagnostics> Inspect(
        string query,
        CancellationToken cancellationToken = default);
}
