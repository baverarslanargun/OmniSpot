using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

internal sealed class FileIndexReader(IReadOnlyList<(FileIndex Index, IReadOnlyList<string>? Scope)> volumes) :
    IQueryCatalogSnapshot, ILiteralNameQuerySnapshot, IQuerySnapshotLease, ITokenIndexSnapshot
{
    public int ItemCount => volumes.Sum(volume => volume.Index.Count);

    public int TokenCount => 0;

    public bool IsTokenIndexReady => true;

    public ISearchStateReader ForQuery() => this;

    public IReadOnlyList<SearchResult> SearchLiteralNames(string query, int limit, ResultView view, CancellationToken cancellationToken)
    {
        if (volumes.Count == 1) return volumes[0].Index.Search(query, limit, view, volumes[0].Scope, cancellationToken);
        return volumes.SelectMany(volume => volume.Index.Search(query, limit, view, volume.Scope, cancellationToken))
            .OrderBy(result => result, SortedResultOrder.For(view.Sort)).Take(limit).ToArray();
    }

    public bool TryGetItem(string path, out SearchItem item)
    {
        foreach (var (index, scope) in volumes)
            if (InScope(path, scope) && index.TryGetItem(path, out item)) return true;
        item = null!;
        return false;
    }

    public bool ContainsPath(string path) => TryGetItem(path, out _);

    public IReadOnlyCollection<SearchItem> GetRoots(CancellationToken cancellationToken = default)
    {
        var roots = new List<SearchItem>();
        foreach (var (index, scope) in volumes)
        {
            if (scope is null or { Count: 0 }) { roots.Add(index.RootItem()); continue; }
            foreach (var path in scope)
                if (index.TryGetItem(path, out var item)) roots.Add(item);
        }
        return roots;
    }

    public IReadOnlyCollection<SearchItem> GetChildren(string parentPath, CancellationToken cancellationToken = default)
    {
        foreach (var (index, scope) in volumes)
            if (InScope(parentPath, scope) && index.TryGetItem(parentPath, out _))
                return index.Children(parentPath, cancellationToken);
        return [];
    }

    public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken cancellationToken = default) =>
        volumes.SelectMany(volume => volume.Index.AllItems(cancellationToken).Where(item => InScope(item.FullPath, volume.Scope))).ToArray();

    public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken cancellationToken = default)
    {
        foreach (var (index, scope) in volumes)
            if (InScope(item.FullPath, scope) && index.TryGetItem(item.FullPath, out _))
                return index.Descendants(item.FullPath, cancellationToken);
        return [];
    }

    public IEnumerable<SearchItem> GetItems(QueryCatalogFilter filter, CancellationToken cancellationToken = default)
    {
        foreach (var (index, scope) in volumes)
        foreach (var item in index.Items(filter, scope, cancellationToken))
            yield return item;
    }

    public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken cancellationToken = default) =>
        Tokens(token, TokenMatch.Exact, 0, cancellationToken);

    public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken cancellationToken = default) =>
        Tokens(token, TokenMatch.Partial, 0, cancellationToken);

    public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken cancellationToken = default) =>
        Tokens(token, TokenMatch.Fuzzy, maxDistance, cancellationToken);

    private IReadOnlyCollection<SearchItem> Tokens(string token, TokenMatch mode, int maxDistance, CancellationToken cancellationToken) =>
        volumes.Count == 1
            ? volumes[0].Index.TokenItems(token, mode, maxDistance, volumes[0].Scope, cancellationToken)
            : volumes.SelectMany(volume => volume.Index.TokenItems(token, mode, maxDistance, volume.Scope, cancellationToken)).ToArray();

    public ISearchStateReader WithUpserts(IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer) => throw new NotSupportedException();

    public ISearchStateReader WithoutPathAndDescendants(string path) => throw new NotSupportedException();

    public ISearchStateReader WithChanges(IEnumerable<string> removedPaths, IEnumerable<FileSystemNode> upserts, ITokenizer tokenizer) =>
        throw new NotSupportedException();

    private static bool InScope(string path, IReadOnlyList<string>? scope)
    {
        if (scope is null or { Count: 0 }) return true;
        var full = Path.TrimEndingDirectorySeparator(path);
        foreach (var root in scope)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(root);
            if (full.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public void Dispose() { }
}
