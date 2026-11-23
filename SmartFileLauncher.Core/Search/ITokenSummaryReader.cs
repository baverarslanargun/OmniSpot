namespace SmartFileLauncher.Core.Search;

public readonly record struct TokenSummary(int Count, IReadOnlyList<string> SampleNames);

public interface ITokenSummaryReader
{
    TokenSummary SummarizeToken(string token, int sampleCount, CancellationToken cancellationToken = default);
}
