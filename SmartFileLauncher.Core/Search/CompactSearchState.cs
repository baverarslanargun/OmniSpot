using System.Collections.Immutable;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Utilities;

namespace SmartFileLauncher.Core.Search;

internal sealed class CompactSearchState : IIndexCatalogSnapshot
{
    private static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
    private sealed record Entry(SearchItem? Item, string[] Tokens, int Id);
    private readonly CompactCatalog _catalog;
    private readonly ImmutableDictionary<string, Entry> _delta;
    private readonly ImmutableHashSet<int> _suppressed;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _postings;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _children;
    private readonly int _missingParents;
    private readonly int _nextId;
    private readonly bool _varint;
    private readonly Dictionary<int, SearchItem>? _queryItems;
    private readonly Dictionary<int, string>? _queryPaths;

    public int ItemCount { get; }
    public int TokenCount { get; }
    internal int QueryItemCacheCount => _queryItems?.Count ?? 0;
    internal int QueryPathCacheCount => _queryPaths?.Count ?? 0;
    internal static CompactSearchState Empty { get; } =
        Create(Array.Empty<SearchItem>(), new BasicTokenizer());
    public bool ContainsPath(string path) => Find(path) != null;
    public bool TryGetItem(string path, out SearchItem item)
    {
        ArgumentNullException.ThrowIfNull(path);
        var found = Find(path);
        if (found == null)
        {
            item = null!;
            return false;
        }
        item = found;
        return true;
    }

    ISearchStateReader ISearchStateReader.WithUpserts(
        IEnumerable<FileSystemNode> nodes,
        ITokenizer tokenizer) => WithUpserts(nodes, tokenizer);

    ISearchStateReader ISearchStateReader.WithoutPathAndDescendants(string path) =>
        WithoutPathAndDescendants(path);

    ISearchStateReader ISearchStateReader.WithChanges(
        IEnumerable<string> removedPaths,
        IEnumerable<FileSystemNode> upserts,
        ITokenizer tokenizer) => WithChanges(removedPaths, upserts, tokenizer);

    IIndexCatalogSnapshot IIndexCatalogSnapshot.WithRecordUpserts(
        IEnumerable<SearchItem> items,
        ITokenizer tokenizer) => WithRecordUpserts(items, tokenizer);

    IIndexCatalogSnapshot IIndexCatalogSnapshot.WithRecordChanges(
        IEnumerable<string> removedPaths,
        IEnumerable<SearchItem> upserts,
        ITokenizer tokenizer) => WithRecordChanges(removedPaths, upserts, tokenizer);

    IIndexCatalogSnapshot IIndexCatalogSnapshot.WithoutRecordPathAndDescendants(string path) =>
        WithoutPathAndDescendants(path);

    internal CompactSearchState WithChanges(
        IEnumerable<string> removedPaths,
        IEnumerable<FileSystemNode> upserts,
        ITokenizer tokenizer) => WithRecordChanges(
            removedPaths,
            upserts.Select(SearchItem.FromNode),
            tokenizer);

    internal CompactSearchState WithRecordChanges(
        IEnumerable<string> removedPaths,
        IEnumerable<SearchItem> upserts,
        ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(removedPaths);
        ArgumentNullException.ThrowIfNull(upserts);
        ArgumentNullException.ThrowIfNull(tokenizer);
        var state = this;
        foreach (var path in removedPaths)
            state = state.Change(path, null, []);
        return (state.DeltaCount >= DeltaCapacity ? state.Compact() : state)
            .WithRecordUpserts(upserts, tokenizer);
    }
    internal int DeltaCount => _delta.Count;
    internal int DeltaCapacity { get; }
    internal int MissingParentCount => _missingParents;
    internal long Generation { get; }
    internal int PayloadBytes => _catalog.PayloadBytes;

    private CompactSearchState(CompactCatalog catalog, int capacity, bool varint, long? generation = null)
        : this(catalog, ImmutableDictionary.Create<string, Entry>(Comparer), ImmutableHashSet<int>.Empty,
            ImmutableDictionary.Create<string, ImmutableHashSet<string>>(Comparer),
            ImmutableDictionary.Create<string, ImmutableHashSet<string>>(Comparer),
            catalog.ItemCount, catalog.TokenCount, catalog.MissingParentCount, catalog.ItemCount,
            capacity, varint, generation ?? catalog.Generation) { }

    private CompactSearchState(CompactCatalog catalog, ImmutableDictionary<string, Entry> delta,
        ImmutableHashSet<int> suppressed, ImmutableDictionary<string, ImmutableHashSet<string>> postings,
        ImmutableDictionary<string, ImmutableHashSet<string>> children, int itemCount, int tokenCount,
        int missingParents, int nextId, int capacity, bool varint, long generation, bool queryView = false)
    {
        _catalog = catalog; _delta = delta; _suppressed = suppressed;
        _postings = postings; _children = children; ItemCount = itemCount; TokenCount = tokenCount;
        _missingParents = missingParents; _nextId = nextId; DeltaCapacity = capacity; _varint = varint;
        Generation = generation;
        if (queryView) { _queryItems = []; _queryPaths = []; }
    }

    ISearchStateReader ISearchStateReader.ForQuery() => new CompactSearchState(_catalog, _delta, _suppressed,
        _postings, _children, ItemCount, TokenCount, _missingParents, _nextId, DeltaCapacity, _varint, Generation, queryView: true);

    private SearchItem ReadItem(int id)
    {
        if (_queryItems == null) return _catalog.GetItem(id);
        if (!_queryItems.TryGetValue(id, out var item)) _queryItems[id] = item = _catalog.GetItem(id, _queryPaths);
        return item;
    }

    internal static CompactSearchState Create(
        IEnumerable<FileSystemNode> nodes,
        ITokenizer tokenizer,
        int deltaCapacity = 4096,
        bool varint = false)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return Create(nodes.Select(SearchItem.FromNode), tokenizer, deltaCapacity, varint);
    }

    internal static CompactSearchState Create(IEnumerable<SearchItem> items, ITokenizer tokenizer,
        int deltaCapacity = 4096, bool varint = false)
    {
        if (deltaCapacity < 1) throw new ArgumentOutOfRangeException(nameof(deltaCapacity));
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(tokenizer);
        var distinctItems = items.GroupBy(item => item.FullPath, Comparer)
            .Select(group => group.Last()).ToArray();
        return new(CompactCatalog.Create(distinctItems, tokenizer, varint), deltaCapacity, varint);
    }

    internal static CompactSearchState OpenMapped(string path, int deltaCapacity = 4096)
    {
        if (deltaCapacity < 1) throw new ArgumentOutOfRangeException(nameof(deltaCapacity));
        var catalog = CompactCatalog.OpenMapped(path);
        return new(catalog, deltaCapacity, catalog.UsesVarint);
    }

    internal void WriteNewBase(string path) =>
        (_delta.Count == 0 ? this : Compact())._catalog.WriteNew(path);

    internal CompactSearchState Compact() => new(
        CompactCatalog.Create(GetAllItems().ToArray(), item => TokensFor(item.FullPath), _varint, Generation + 1),
        DeltaCapacity, _varint, Generation + 1);

    internal CompactSearchState CompactToNewMapped(string path) => new(
        CompactCatalog.CreateMapped(path, GetAllItems().ToArray(), item => TokensFor(item.FullPath),
            _varint, Generation + 1),
        DeltaCapacity, _varint, Generation + 1);

    private string[] TokensFor(string path) => _delta.TryGetValue(path, out var entry)
        ? entry.Tokens : _catalog.ItemTokens(_catalog.Find(path));

    internal (long Generation, int Id)? Identity(string path)
    {
        if (_delta.TryGetValue(path, out var entry))
            return entry.Item == null ? null : (Generation, entry.Id);
        var id = _catalog.Find(path);
        return id < 0 ? null : (Generation, id);
    }

    private SearchItem? Find(string path)
    {
        if (_delta.TryGetValue(path, out var entry)) return entry.Item;
        var id = _catalog.Find(path);
        return id < 0 ? null : ReadItem(id);
    }

    private bool HasToken(string token) => (_postings.TryGetValue(token, out var paths) && paths.Count != 0) ||
        _catalog.Posting(token).Any(id => !_suppressed.Contains(id));

    public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken cancellationToken = default)
    {
        var result = new List<SearchItem>();
        foreach (var id in _catalog.Posting(token))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_suppressed.Contains(id)) result.Add(ReadItem(id));
        }
        if (_postings.TryGetValue(token, out var paths))
        {
            foreach (var path in paths)
            {
                cancellationToken.ThrowIfCancellationRequested();
                result.Add(_delta[path].Item!);
            }
        }
        return result;
    }

    public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken cancellationToken = default) =>
        MatchTerms(value => value.Contains(token, StringComparison.OrdinalIgnoreCase), cancellationToken);

    public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2,
        CancellationToken cancellationToken = default) => HasToken(token)
        ? Get(token, cancellationToken)
        : MatchTerms(value => FuzzyMatcher.IsFuzzyMatch(token, value, maxDistance), cancellationToken);

    private IReadOnlyCollection<SearchItem> MatchTerms(Func<string, bool> predicate, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, SearchItem>(Comparer);
        foreach (var token in _catalog.Tokens.Concat(_postings.Keys).Distinct(Comparer))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!predicate(token)) continue;
            foreach (var item in Get(token, cancellationToken)) result[item.FullPath] = item;
        }
        return result.Values.ToArray();
    }

    public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken cancellationToken = default)
    {
        var items = new List<SearchItem>(ItemCount);
        for (var id = 0; id < _catalog.ItemCount; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_suppressed.Contains(id)) items.Add(ReadItem(id));
        }
        foreach (var entry in _delta.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Item != null) items.Add(entry.Item);
        }
        return items;
    }

    IEnumerable<SearchItem> IQueryCatalogSnapshot.GetItems(
        QueryCatalogFilter filter,
        CancellationToken cancellationToken)
    {
        var sharedPrefixPaths = new Dictionary<int, string>();
        for (var id = 0; id < _catalog.ItemCount; id++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_suppressed.Contains(id) && _catalog.Matches(id, filter))
            {
                yield return _catalog.GetTransientItem(id, sharedPrefixPaths);
            }
        }

        foreach (var entry in _delta.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Item is { } item && filter.Matches(item))
            {
                yield return item;
            }
        }
    }

    public IReadOnlyCollection<SearchItem> GetRoots(
        CancellationToken cancellationToken = default)
    {
        var roots = new List<SearchItem>();
        foreach (var id in _catalog.Roots())
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!_suppressed.Contains(id)) roots.Add(ReadItem(id));
        }
        foreach (var entry in _delta.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (entry.Item is { } item && string.IsNullOrEmpty(item.ParentPath))
                roots.Add(item);
        }
        return roots;
    }

    public IReadOnlyCollection<SearchItem> GetChildren(
        string parentPath,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(parentPath);
        var children = new List<SearchItem>();
        foreach (var child in Children(parentPath))
        {
            cancellationToken.ThrowIfCancellationRequested();
            children.Add(child);
        }
        return children;
    }

    private IEnumerable<SearchItem> Children(string path)
    {
        foreach (var id in _catalog.Children(path, _queryPaths))
            if (!_suppressed.Contains(id)) yield return ReadItem(id);
        if (_children.TryGetValue(path, out var paths))
            foreach (var child in paths) yield return _delta[child].Item!;
    }

    public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken cancellationToken = default)
    {
        var result = new List<SearchItem>();
        var seen = new HashSet<string>(Comparer) { item.FullPath };
        var pending = new Stack<SearchItem>(Children(item.FullPath).OrderByDescending(child => child.FullPath, Comparer));
        while (pending.TryPop(out var child))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!seen.Add(child.FullPath)) continue;
            result.Add(child);
            foreach (var descendant in Children(child.FullPath).OrderByDescending(value => value.FullPath, Comparer))
                pending.Push(descendant);
        }
        return result;
    }

    internal CompactSearchState WithUpserts(
        IEnumerable<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        return WithRecordUpserts(nodes.Select(SearchItem.FromNode), tokenizer);
    }

    internal CompactSearchState WithRecordUpserts(IEnumerable<SearchItem> items, ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(items);
        ArgumentNullException.ThrowIfNull(tokenizer);
        var state = this;
        foreach (var item in items.GroupBy(item => item.FullPath, Comparer)
                     .Select(group => group.Last()).OrderBy(item => item.FullPath.Length))
        {
            if (state.Find(item.FullPath)?.IsDirectory == true && !item.IsDirectory)
                state = state.WithoutPathAndDescendants(item.FullPath);
            state = state.Change(item.FullPath, item, tokenizer.Tokenize(item.Name).Distinct(Comparer).ToArray());
        }
        return state.DeltaCount >= DeltaCapacity ? state.Compact() : state;
    }

    internal CompactSearchState WithoutPathAndDescendants(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var pathRoot = Path.GetPathRoot(path);
        var normalized = pathRoot != null && Comparer.Equals(pathRoot, path)
            ? pathRoot : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        IEnumerable<SearchItem> removed;
        if (_missingParents != 0)
        {
            var endsWithSeparator = normalized.EndsWith(Path.DirectorySeparatorChar) ||
                normalized.EndsWith(Path.AltDirectorySeparatorChar);
            var prefix = endsWithSeparator ? normalized : normalized + Path.DirectorySeparatorChar;
            var alternatePrefix = endsWithSeparator ? null : normalized + Path.AltDirectorySeparatorChar;
            removed = GetAllItems().Where(item => Comparer.Equals(item.FullPath, normalized) ||
                item.FullPath.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) ||
                (alternatePrefix != null && item.FullPath.StartsWith(alternatePrefix, StringComparison.OrdinalIgnoreCase)));
        }
        else
        {
            var root = Find(normalized);
            removed = root == null ? [] : GetDescendants(root).Append(root);
        }
        var state = this;
        foreach (var item in removed.OrderByDescending(item => item.FullPath.Length))
            state = state.Change(item.FullPath, null, []);
        return state.DeltaCount >= DeltaCapacity ? state.Compact() : state;
    }

    private CompactSearchState Change(string path, SearchItem? item, string[] tokens)
    {
        var old = Find(path);
        if (old == null && item == null) return this;
        var baseId = _catalog.Find(path);
        var oldTokens = _delta.TryGetValue(path, out var previous) ? previous.Tokens :
            baseId >= 0 ? _catalog.ItemTokens(baseId) : [];
        var affected = oldTokens.Concat(tokens).Distinct(Comparer).ToArray();
        var previousTokenCount = affected.Count(HasToken);
        var missing = _missingParents;
        if (old?.ParentPath is { Length: > 0 } oldParent && Find(oldParent) == null)
            missing--;
        if (old == null && item != null) missing -= Children(path).Count();
        if (old != null && item == null) missing += Children(path).Count();
        if (item?.ParentPath is { Length: > 0 } newParent &&
            !Comparer.Equals(newParent, path) && Find(newParent) == null) missing++;

        var postings = _postings;
        foreach (var token in oldTokens) postings = Remove(postings, token, path);
        foreach (var token in tokens) postings = Add(postings, token, path);
        var children = _children;
        if (old?.ParentPath != null) children = Remove(children, old.ParentPath, path);
        if (item?.ParentPath != null) children = Add(children, item.ParentPath, path);
        var id = old != null ? previous?.Id ?? baseId : _nextId;
        var nextId = old == null && item != null ? checked(_nextId + 1) : _nextId;
        var state = new CompactSearchState(_catalog, _delta.SetItem(path, new(item, tokens, id)),
            baseId >= 0 ? _suppressed.Add(baseId) : _suppressed, postings, children,
            ItemCount + (old == null ? 1 : 0) - (item == null ? 1 : 0), TokenCount,
            missing, nextId, DeltaCapacity, _varint, Generation);
        var newTokenCount = TokenCount - previousTokenCount + affected.Count(state.HasToken);
        return new(_catalog, state._delta, state._suppressed, postings, children, state.ItemCount, newTokenCount,
            missing, nextId, DeltaCapacity, _varint, Generation);
    }

    private static ImmutableDictionary<string, ImmutableHashSet<string>> Add(
        ImmutableDictionary<string, ImmutableHashSet<string>> map, string key, string path) =>
        map.SetItem(key, (map.TryGetValue(key, out var paths) ? paths : ImmutableHashSet.Create<string>(Comparer)).Add(path));

    private static ImmutableDictionary<string, ImmutableHashSet<string>> Remove(
        ImmutableDictionary<string, ImmutableHashSet<string>> map, string key, string path)
    {
        if (!map.TryGetValue(key, out var paths)) return map;
        paths = paths.Remove(path);
        return paths.Count == 0 ? map.Remove(key) : map.SetItem(key, paths);
    }
}
