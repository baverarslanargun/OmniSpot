using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.Core.Application.Search;

public sealed record SearchRequest(
    string Query,
    bool NaturalLanguageMode,
    bool HasInternetConnection,
    int MaxResults = 100,
    string? ReasoningEffort = null,
    ItemFilter? Filter = null,
    ItemSort? Sort = null)
{
    public ResultView View => new(Filter ?? ItemFilter.None, Sort ?? ItemSort.Relevance);
}
