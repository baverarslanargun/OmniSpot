using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Application.Search;

public enum SearchExecutionMode
{
    Standard,
    Advanced,
    OfflineFallback,
    RuleBasedFallback
}

public sealed record SearchOutcome(
    IReadOnlyList<SearchResult> Results,
    SearchExecutionMode Mode,
    bool UsedFallback,
    string? FallbackReason,
    string? WarningMessage,
    string? AutoOpenPath,
    StructuredQuery? StructuredQuery)
{
    public bool IsPageable => StructuredQuery is null;
    internal LiteralRanking? LiteralRanking { get; init; }
}
