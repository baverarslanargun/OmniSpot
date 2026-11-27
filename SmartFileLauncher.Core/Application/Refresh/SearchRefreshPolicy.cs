using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Application.Refresh;

public static class SearchRefreshPolicy
{
    private static readonly BasicTokenizer Tokenizer = new();

    public static bool ShouldRefresh(FileChangeEvent change, SearchRequest? request, bool literalNamesOnly,
        SearchOutcome? outcome = null)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Query)) return false;
        if (!literalNamesOnly || change.IsDirectory ||
            request.View.Filter.Date != FilterDateRange.Any || change.AffectedNames == null) return true;
        if (request.NaturalLanguageMode)
        {
            if (outcome == null) return true;
            if (outcome.StructuredQuery is { } query)
                return AdvancedSearchEngine.MayMatchChangedNames(query, change.AffectedNames, Tokenizer);
        }

        var names = NameQuery.Create(request.Query)!;
        return change.AffectedNames.Any(names.Matches);
    }
}
