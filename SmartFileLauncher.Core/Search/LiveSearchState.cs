using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search;

internal sealed class LiveSearchState : IQueryCatalogSnapshot
{
    private readonly IQueryCatalogSnapshot[] _parts;
    private readonly Lazy<int> _tokens;
    internal LiveSearchState(CompactSearchState[] parts)
    {
        _parts = parts; ItemCount = parts.Sum(part => part.ItemCount);
        _tokens = new(() => parts.Length == 1 ? parts[0].TokenCount : parts.SelectMany(part => part.BaseTokens).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
    private LiveSearchState(IQueryCatalogSnapshot[] parts, Lazy<int> tokens, int count)
    { _parts = parts; _tokens = tokens; ItemCount = count; }
    public int ItemCount { get; }
    public int TokenCount => _tokens.Value;
    public ISearchStateReader ForQuery() => new LiveSearchState(_parts.Select(part => (IQueryCatalogSnapshot)part.ForQuery()).ToArray(), _tokens, ItemCount);
    public bool TryGetItem(string path, out SearchItem item)
    { foreach (var part in _parts) if (part.TryGetItem(path, out item)) return true; item = null!; return false; }
    public bool ContainsPath(string path) => _parts.Any(part => part.ContainsPath(path));
    public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken cancellationToken = default) => _parts.SelectMany(part => part.Get(token, cancellationToken)).ToArray();
    public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken cancellationToken = default) => _parts.SelectMany(part => part.GetPartial(token, cancellationToken)).ToArray();
    public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken cancellationToken = default)
    { var exact = Get(token, cancellationToken); return exact.Count > 0 ? exact : _parts.SelectMany(part => part.GetFuzzy(token, maxDistance, cancellationToken)).ToArray(); }
    public IReadOnlyCollection<SearchItem> GetRoots(CancellationToken cancellationToken = default) => _parts.SelectMany(part => part.GetRoots(cancellationToken)).ToArray();
    public IReadOnlyCollection<SearchItem> GetChildren(string parentPath, CancellationToken cancellationToken = default) => _parts.SelectMany(part => part.GetChildren(parentPath, cancellationToken)).ToArray();
    public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken cancellationToken = default) => _parts.SelectMany(part => part.GetDescendants(item, cancellationToken)).ToArray();
    public IEnumerable<SearchItem> GetItems(QueryCatalogFilter filter, CancellationToken cancellationToken = default) => _parts.SelectMany(part => part.GetItems(filter, cancellationToken));
    public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken cancellationToken = default) => _parts.SelectMany(part => part.GetAllItems(cancellationToken)).ToArray();
    public ISearchStateReader WithUpserts(IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer) => throw new NotSupportedException("Live güncellemesi kalıcı store üzerinden yapılmalı.");
    public ISearchStateReader WithoutPathAndDescendants(string path) => throw new NotSupportedException("Live güncellemesi kalıcı store üzerinden yapılmalı.");
    public ISearchStateReader WithChanges(IEnumerable<string> removedPaths, IEnumerable<FileSystemNode> upserts, ITokenizer tokenizer) => throw new NotSupportedException("Live güncellemesi kalıcı store üzerinden yapılmalı.");
}
