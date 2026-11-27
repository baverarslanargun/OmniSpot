namespace SmartFileLauncher.Core.Search;

internal interface ITokenPartialSnapshot
{
    IReadOnlyList<(SearchItem Item, bool Exact)> GetWithPartial(
        string token, QueryCatalogFilter? filter, out bool found, CancellationToken cancellationToken);
}
