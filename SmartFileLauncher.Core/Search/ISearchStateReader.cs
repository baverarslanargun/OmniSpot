using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Utilities;

namespace SmartFileLauncher.Core.Search;

public enum SearchStateLayout
{
    Legacy,
    Compact
}

public interface ISearchStateReader
{
    int ItemCount { get; }
    int TokenCount { get; }
    ISearchStateReader ForQuery() => this;
    IReadOnlyCollection<SearchItem> Get(string token, CancellationToken cancellationToken = default);
    IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken cancellationToken = default);
    IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken cancellationToken = default);
    IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken cancellationToken = default);
    IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken cancellationToken = default);

    bool ContainsPath(string path);
    ISearchStateReader WithUpserts(IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer);
    ISearchStateReader WithoutPathAndDescendants(string path);
    ISearchStateReader WithChanges(
        IEnumerable<string> removedPaths,
        IEnumerable<FileSystemNode> upserts,
        ITokenizer tokenizer);

    static ISearchStateReader Empty(SearchStateLayout layout) => layout switch
    {
        SearchStateLayout.Compact => CompactSearchState.Empty,
        _ => SearchState.Empty
    };

    static ISearchStateReader Create(
        SearchStateLayout layout,
        IEnumerable<FileSystemNode> nodes,
        ITokenizer tokenizer) => layout switch
    {
        SearchStateLayout.Compact => CompactSearchState.Create(nodes, tokenizer),
        _ => SearchState.Create(nodes, tokenizer)
    };
}
