using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search;

internal sealed class SortedResultOrder : IComparer<SearchResult>
{
    private readonly ItemSort _sort;

    private SortedResultOrder(ItemSort sort) => _sort = sort;

    internal static IComparer<SearchResult> For(ItemSort sort) =>
        sort.IsRelevance ? SearchResultOrder.Instance : new SortedResultOrder(sort);

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

        return _sort.Compare(Row(x), Row(y));
    }

    private static SortRow Row(SearchResult result) => new(
        result.Name,
        result.FullPath,
        result.IsDirectory,
        result.SizeBytes,
        result.LastWriteTime,
        result.Score);
}
