using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search.Paged;

internal sealed class PagedSearchState : IDisposable
{
    private readonly (CatalogStore Store, bool ExcludeRoot)[] _parts;
    private readonly object _gate = new();
    private bool _disposed;
    internal PagedSearchState(IEnumerable<(CatalogStore Store, bool ExcludeRoot)> parts) => _parts = parts.ToArray();

    internal CatalogStore? Reuse(string directory, string frame)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            foreach (var part in _parts)
            {
                using var catalog = part.Store.Acquire();
                if (catalog.Data.Directory != directory) continue;
                if (catalog.Data.Frame == frame) return new CatalogStore(catalog, false);
                using var next = new Catalog(Image.Open(directory, frame, catalog.Data));
                next.ReuseLiteralNameIndex(catalog);
                return new CatalogStore(next, false);
            }
            return null;
        }
    }

    internal CompositeReader Acquire()
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            var readers = new List<Reader>();
            try
            {
                foreach (var part in _parts) readers.Add(new Reader(part.Store.Acquire(), true, part.ExcludeRoot));
                return new CompositeReader(readers.ToArray());
            }
            catch { foreach (var reader in readers) reader.Dispose(); throw; }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            foreach (var part in _parts) part.Store.Dispose();
        }
    }
}

internal sealed class CompositeReader(Reader[] parts) : IQueryCatalogSnapshot, IQuerySnapshotLease, IStandardQuerySnapshot, ILiteralNameQuerySnapshot, ITokenIndexSnapshot
{
    private int _disposed;
    private readonly Lazy<int> _tokens = new(() => parts.Length == 1 ? parts[0].TokenCount :
        parts.SelectMany(part => part.Tokens).Distinct(StringComparer.OrdinalIgnoreCase).Count());
    public int ItemCount => parts.Sum(part => part.ItemCount);
    public int TokenCount => _tokens.Value;
    public bool IsTokenIndexReady => parts.All(part => part.IsTokenIndexReady);
    private void RequireTokens() { if (!IsTokenIndexReady) throw new TokenIndexNotReadyException(); }
    public ISearchStateReader ForQuery() { ObjectDisposedException.ThrowIf(_disposed != 0, this); return this; }
    public IReadOnlyList<SearchResult> SearchStandard(string query, int limit, ResultView view, CancellationToken ct) =>
        parts.SelectMany(part => part.SearchStandard(query, limit, view, ct))
            .OrderBy(result => result, SortedResultOrder.For(view.Sort)).Take(limit).ToArray();
    public IReadOnlyList<SearchResult> SearchLiteralNames(string query, int limit, ResultView view, CancellationToken ct)
    {
        var results = parts.SelectMany(part => part.SearchLiteralNames(query, limit, view, ct))
            .OrderBy(result => result, SortedResultOrder.For(view.Sort)).Take(limit).ToArray();
        ct.ThrowIfCancellationRequested();
        return results;
    }
    public bool TryGetItem(string path, out SearchItem item)
    {
        foreach (var part in parts) if (part.TryGetItem(path, out item)) return true;
        item = null!; return false;
    }
    public bool ContainsPath(string path) => TryGetItem(path, out _);
    public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken ct = default)
    { RequireTokens(); return parts.SelectMany(part => part.Get(token, ct)).ToArray(); }
    public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken ct = default)
    { RequireTokens(); return parts.SelectMany(part => part.GetPartial(token, ct)).ToArray(); }
    public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken ct = default)
    {
        RequireTokens();
        var exact = Get(token, ct);
        return exact.Count > 0 ? exact : parts.SelectMany(part => part.GetFuzzy(token, maxDistance, ct)).ToArray();
    }
    public IReadOnlyCollection<SearchItem> GetRoots(CancellationToken ct = default) => parts.SelectMany(part => part.GetRoots(ct)).ToArray();
    public IReadOnlyCollection<SearchItem> GetChildren(string path, CancellationToken ct = default) => parts.SelectMany(part => part.GetChildren(path, ct)).ToArray();
    public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken ct = default) => parts.SelectMany(part => part.GetAllItems(ct)).ToArray();
    public IEnumerable<SearchItem> GetItems(QueryCatalogFilter filter, CancellationToken ct = default) => parts.SelectMany(part => part.GetItems(filter, ct));
    public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken ct = default) =>
        parts.SelectMany(part => part.RootPath.StartsWith(Path.TrimEndingDirectorySeparator(item.FullPath) + "\\", StringComparison.OrdinalIgnoreCase)
            ? part.GetAllItems(ct) : part.GetDescendants(item, ct)).ToArray();
    public ISearchStateReader WithUpserts(IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer) => throw new NotSupportedException();
    public ISearchStateReader WithoutPathAndDescendants(string path) => throw new NotSupportedException();
    public ISearchStateReader WithChanges(IEnumerable<string> paths, IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer) => throw new NotSupportedException();
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0) foreach (var part in parts) part.Dispose(); }
}
