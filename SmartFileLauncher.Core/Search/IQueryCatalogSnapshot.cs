namespace SmartFileLauncher.Core.Search;

internal interface IQueryCatalogSnapshot : ISearchStateReader
{
    bool TryGetItem(string path, out SearchItem item);
    IReadOnlyCollection<SearchItem> GetRoots(
        CancellationToken cancellationToken = default);
    IReadOnlyCollection<SearchItem> GetChildren(
        string parentPath,
        CancellationToken cancellationToken = default);

    IEnumerable<SearchItem> GetItems(
        QueryCatalogFilter filter,
        CancellationToken cancellationToken = default)
    {
        foreach (var item in GetAllItems(cancellationToken))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (filter.Matches(item)) yield return item;
        }
    }
}

internal interface IIndexCatalogSnapshot : IQueryCatalogSnapshot
{
    IIndexCatalogSnapshot WithRecordUpserts(
        IEnumerable<SearchItem> items,
        ITokenizer tokenizer);
    IIndexCatalogSnapshot WithRecordChanges(
        IEnumerable<string> removedPaths,
        IEnumerable<SearchItem> upserts,
        ITokenizer tokenizer);
    IIndexCatalogSnapshot WithoutRecordPathAndDescendants(string path);
}
