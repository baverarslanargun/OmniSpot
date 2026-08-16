using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search;

internal sealed class SearchResultOrder : IComparer<SearchResult>
{
    internal static readonly SearchResultOrder Instance = new();

    private SearchResultOrder()
    {
    }

    public int Compare(SearchResult? x, SearchResult? y)
    {
        if (ReferenceEquals(x, y))
        {
            return 0;
        }

        if (x is null)
        {
            return 1;
        }

        if (y is null)
        {
            return -1;
        }

        var byScore = y.Score.CompareTo(x.Score);
        if (byScore != 0)
        {
            return byScore;
        }

        var byName = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        if (byName != 0)
        {
            return byName;
        }

        var byPath = string.Compare(x.FullPath, y.FullPath, StringComparison.OrdinalIgnoreCase);
        if (byPath != 0)
        {
            return byPath;
        }

        return string.CompareOrdinal(x.FullPath, y.FullPath);
    }
}
