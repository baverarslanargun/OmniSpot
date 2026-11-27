using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Application.Refresh;

public static class SearchRefreshPolicy
{
    public static bool ShouldRefresh(FileChangeEvent change, SearchRequest? request, bool literalNamesOnly)
    {
        if (request == null || string.IsNullOrWhiteSpace(request.Query)) return false;
        if (!literalNamesOnly || request.NaturalLanguageMode || change.IsDirectory ||
            request.View.Filter.Date != FilterDateRange.Any || change.AffectedNames == null) return true;

        var names = NameQuery.Create(request.Query)!;
        return change.AffectedNames.Any(names.Matches);
    }
}
