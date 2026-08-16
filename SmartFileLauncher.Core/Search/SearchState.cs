using System.Collections.Immutable;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Utilities;

namespace SmartFileLauncher.Core.Search;

public sealed class SearchState
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ImmutableDictionary<string, SearchItem> _itemsByPath;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _pathsByToken;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _tokensByPath;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _childrenByPath;

    private SearchState(
        ImmutableDictionary<string, SearchItem> itemsByPath,
        ImmutableDictionary<string, ImmutableHashSet<string>> pathsByToken,
        ImmutableDictionary<string, ImmutableHashSet<string>> tokensByPath,
        ImmutableDictionary<string, ImmutableHashSet<string>> childrenByPath)
    {
        _itemsByPath = itemsByPath;
        _pathsByToken = pathsByToken;
        _tokensByPath = tokensByPath;
        _childrenByPath = childrenByPath;
    }

    public static SearchState Empty { get; } = new(
        ImmutableDictionary.Create<string, SearchItem>(PathComparer),
        ImmutableDictionary.Create<string, ImmutableHashSet<string>>(PathComparer),
        ImmutableDictionary.Create<string, ImmutableHashSet<string>>(PathComparer),
        ImmutableDictionary.Create<string, ImmutableHashSet<string>>(PathComparer));

    public int ItemCount => _itemsByPath.Count;

    public IReadOnlyCollection<SearchItem> Get(
        string token,
        CancellationToken cancellationToken = default) =>
        GetItemsForPaths(_pathsByToken.TryGetValue(token, out var paths)
            ? paths
            : Array.Empty<string>(), cancellationToken);

    public IReadOnlyCollection<SearchItem> GetPartial(
        string token,
        CancellationToken cancellationToken = default)
    {
        var paths = new HashSet<string>(PathComparer);
        foreach (var (indexedToken, indexedPaths) in _pathsByToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (indexedToken.Contains(token, StringComparison.OrdinalIgnoreCase))
            {
                paths.UnionWith(indexedPaths);
            }
        }

        return GetItemsForPaths(paths, cancellationToken);
    }

    public IReadOnlyCollection<SearchItem> GetFuzzy(
        string token,
        int maxDistance = 2,
        CancellationToken cancellationToken = default)
    {
        if (_pathsByToken.TryGetValue(token, out var exactPaths))
        {
            return GetItemsForPaths(exactPaths, cancellationToken);
        }

        var paths = new HashSet<string>(PathComparer);
        foreach (var (indexedToken, indexedPaths) in _pathsByToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (FuzzyMatcher.IsFuzzyMatch(token, indexedToken, maxDistance))
            {
                paths.UnionWith(indexedPaths);
            }
        }

        return GetItemsForPaths(paths, cancellationToken);
    }

    public IReadOnlyCollection<SearchItem> GetAllItems(
        CancellationToken cancellationToken = default)
    {
        var items = new List<SearchItem>(_itemsByPath.Count);
        foreach (var item in _itemsByPath.Values)
        {
            cancellationToken.ThrowIfCancellationRequested();
            items.Add(item);
        }

        return items;
    }

    public IReadOnlyCollection<SearchItem> GetDescendants(
        SearchItem item,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(item);

        var result = new List<SearchItem>();
        var pending = new Stack<string>(GetChildren(item.FullPath)
            .OrderByDescending(path => path, PathComparer));
        while (pending.Count > 0)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var path = pending.Pop();
            if (!_itemsByPath.TryGetValue(path, out var child))
            {
                continue;
            }

            result.Add(child);
            foreach (var descendant in GetChildren(path).OrderByDescending(value => value, PathComparer))
            {
                pending.Push(descendant);
            }
        }

        return result;
    }

    public static SearchState Create(
        IEnumerable<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(tokenizer);

        var sourceItems = ToDistinctItems(nodes);
        var items = ImmutableDictionary.CreateBuilder<string, SearchItem>(PathComparer);
        var pathBuildersByToken = new Dictionary<string, ImmutableHashSet<string>.Builder>(PathComparer);
        var tokensByPath = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);

        foreach (var item in sourceItems)
        {
            var tokens = Tokenize(item.Name, tokenizer);
            items[item.FullPath] = item;
            tokensByPath[item.FullPath] = tokens;

            foreach (var token in tokens)
            {
                if (!pathBuildersByToken.TryGetValue(token, out var paths))
                {
                    paths = ImmutableHashSet.CreateBuilder<string>(PathComparer);
                    pathBuildersByToken[token] = paths;
                }

                paths.Add(item.FullPath);
            }
        }

        var pathsByToken = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        foreach (var (token, paths) in pathBuildersByToken)
        {
            pathsByToken[token] = paths.ToImmutable();
        }

        return Create(items, pathsByToken, tokensByPath);
    }

    internal static SearchState Create(
        InvertedIndexSnapshot invertedIndex,
        IEnumerable<FileSystemNode> nodes)
    {
        ArgumentNullException.ThrowIfNull(invertedIndex);
        ArgumentNullException.ThrowIfNull(nodes);

        var sourceNodes = nodes.Concat(invertedIndex.Entries.Values.SelectMany(entries => entries));
        var sourceItems = ToDistinctItems(sourceNodes);
        var items = ImmutableDictionary.CreateBuilder<string, SearchItem>(PathComparer);
        var pathsByToken = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        var tokensByPath = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        foreach (var item in sourceItems)
        {
            items[item.FullPath] = item;
        }

        foreach (var (token, indexedNodes) in invertedIndex.Entries)
        {
            foreach (var node in indexedNodes)
            {
                if (!items.ContainsKey(node.FullPath))
                {
                    continue;
                }

                pathsByToken.TryGetValue(token, out var paths);
                pathsByToken[token] = (paths ?? ImmutableHashSet.Create<string>(PathComparer))
                    .Add(node.FullPath);
                tokensByPath.TryGetValue(node.FullPath, out var tokens);
                tokensByPath[node.FullPath] = (tokens ?? ImmutableHashSet.Create<string>(PathComparer))
                    .Add(token);
            }
        }

        return Create(items, pathsByToken, tokensByPath);
    }

    public SearchState WithUpserts(
        IEnumerable<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(tokenizer);

        var upserts = ToDistinctItems(nodes)
            .OrderBy(item => item.FullPath.Length)
            .ToArray();
        var state = this;
        foreach (var item in upserts)
        {
            state = state._itemsByPath.TryGetValue(item.FullPath, out var existing) &&
                    existing.IsDirectory && !item.IsDirectory
                ? state.WithoutPathAndDescendants(item.FullPath)
                : state.WithoutPath(item.FullPath);
        }

        foreach (var item in upserts)
        {
            state = state.WithItem(item, Tokenize(item.Name, tokenizer));
        }

        return state;
    }

    public SearchState WithoutPathAndDescendants(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var pathRoot = Path.GetPathRoot(path);
        var normalizedPath = pathRoot != null &&
                             string.Equals(path, pathRoot, StringComparison.OrdinalIgnoreCase)
            ? pathRoot
            : path.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        var descendantPrefix = normalizedPath + Path.DirectorySeparatorChar;
        var alternateDescendantPrefix = normalizedPath + Path.AltDirectorySeparatorChar;
        var pathsToRemove = _itemsByPath.Keys
            .Where(candidate =>
                string.Equals(candidate, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(alternateDescendantPrefix, StringComparison.OrdinalIgnoreCase))
            .OrderByDescending(candidate => candidate.Length)
            .ToArray();

        var state = this;
        foreach (var pathToRemove in pathsToRemove)
        {
            state = state.WithoutPath(pathToRemove);
        }

        return state;
    }

    private SearchState WithItem(
        SearchItem item,
        ImmutableHashSet<string> tokens)
    {
        var items = _itemsByPath.SetItem(item.FullPath, item);
        var pathsByToken = _pathsByToken;
        foreach (var token in tokens)
        {
            pathsByToken.TryGetValue(token, out var paths);
            pathsByToken = pathsByToken.SetItem(
                token,
                (paths ?? ImmutableHashSet.Create<string>(PathComparer)).Add(item.FullPath));
        }

        var childrenByPath = _childrenByPath;
        if (item.ParentPath != null && items.ContainsKey(item.ParentPath))
        {
            childrenByPath.TryGetValue(item.ParentPath, out var children);
            childrenByPath = childrenByPath.SetItem(
                item.ParentPath,
                (children ?? ImmutableHashSet.Create<string>(PathComparer)).Add(item.FullPath));
        }

        return new SearchState(
            items,
            pathsByToken,
            _tokensByPath.SetItem(item.FullPath, tokens),
            childrenByPath);
    }

    private SearchState WithoutPath(string path)
    {
        if (!_itemsByPath.TryGetValue(path, out var item))
        {
            return this;
        }

        var pathsByToken = _pathsByToken;
        if (_tokensByPath.TryGetValue(path, out var tokens))
        {
            foreach (var token in tokens)
            {
                if (!pathsByToken.TryGetValue(token, out var paths))
                {
                    continue;
                }

                var updatedPaths = paths.Remove(path);
                pathsByToken = updatedPaths.IsEmpty
                    ? pathsByToken.Remove(token)
                    : pathsByToken.SetItem(token, updatedPaths);
            }
        }

        var childrenByPath = _childrenByPath;
        if (item.ParentPath != null && childrenByPath.TryGetValue(item.ParentPath, out var children))
        {
            var updatedChildren = children.Remove(path);
            childrenByPath = updatedChildren.IsEmpty
                ? childrenByPath.Remove(item.ParentPath)
                : childrenByPath.SetItem(item.ParentPath, updatedChildren);
        }

        return new SearchState(
            _itemsByPath.Remove(path),
            pathsByToken,
            _tokensByPath.Remove(path),
            childrenByPath);
    }

    private IReadOnlyCollection<SearchItem> GetItemsForPaths(
        IEnumerable<string> paths,
        CancellationToken cancellationToken)
    {
        var items = new List<SearchItem>();
        foreach (var path in paths)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_itemsByPath.TryGetValue(path, out var item))
            {
                items.Add(item);
            }
        }

        return items;
    }

    private IEnumerable<string> GetChildren(string path) =>
        _childrenByPath.TryGetValue(path, out var children)
            ? children
            : Array.Empty<string>();

    private static SearchState Create(
        ImmutableDictionary<string, SearchItem>.Builder items,
        ImmutableDictionary<string, ImmutableHashSet<string>>.Builder pathsByToken,
        ImmutableDictionary<string, ImmutableHashSet<string>>.Builder tokensByPath)
    {
        var childrenByPath = BuildChildrenByPath(items.Values, items.Keys);
        return new SearchState(
            items.ToImmutable(),
            pathsByToken.ToImmutable(),
            tokensByPath.ToImmutable(),
            childrenByPath);
    }

    private static ImmutableDictionary<string, ImmutableHashSet<string>> BuildChildrenByPath(
        IEnumerable<SearchItem> items,
        IEnumerable<string> itemPaths)
    {
        var paths = itemPaths.ToHashSet(PathComparer);
        var childrenByPath = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        foreach (var item in items)
        {
            if (item.ParentPath == null || !paths.Contains(item.ParentPath))
            {
                continue;
            }

            childrenByPath.TryGetValue(item.ParentPath, out var children);
            childrenByPath[item.ParentPath] = (children ?? ImmutableHashSet.Create<string>(PathComparer))
                .Add(item.FullPath);
        }

        return childrenByPath.ToImmutable();
    }

    private static SearchItem[] ToDistinctItems(IEnumerable<FileSystemNode> nodes) =>
        nodes.Select(SearchItem.FromNode)
            .GroupBy(item => item.FullPath, PathComparer)
            .Select(group => group.Last())
            .ToArray();

    private static ImmutableHashSet<string> Tokenize(
        string value,
        ITokenizer tokenizer) =>
        tokenizer.Tokenize(value)
            .ToImmutableHashSet(PathComparer);
}

public sealed record SearchItem(
    string Name,
    string FullPath,
    bool IsDirectory,
    long? SizeBytes,
    DateTime? CreatedTime,
    DateTime? LastWriteTime,
    int OpenCount,
    string? ParentPath)
{
    internal static SearchItem FromNode(FileSystemNode node)
    {
        ArgumentNullException.ThrowIfNull(node);
        var metadata = node.Metadata;
        return new SearchItem(
            node.Name,
            node.FullPath,
            node.IsDirectory,
            metadata?.SizeBytes,
            metadata?.CreatedTime,
            metadata?.LastWriteTime,
            metadata?.OpenCount ?? 0,
            node.Parent?.FullPath);
    }
}
