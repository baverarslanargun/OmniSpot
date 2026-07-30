namespace SmartFileLauncher.Core.Application.Search;

public sealed record SearchRequest(
    string Query,
    bool NaturalLanguageMode,
    bool HasInternetConnection,
    int MaxResults = 100);
