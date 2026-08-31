using System.Collections.Immutable;
using System.Runtime.InteropServices;
using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Utilities;

namespace SmartFileLauncher.Core.Search;

public sealed class SearchState
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ImmutableDictionary<string, SearchItem> _itemsByPath;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _pathsByToken;

    private readonly ImmutableDictionary<string, ImmutableArray<string>> _tokensByPath;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _childrenByPath;

    private readonly int _missingParentCount;

    private SearchState(
        ImmutableDictionary<string, SearchItem> itemsByPath,
        ImmutableDictionary<string, ImmutableHashSet<string>> pathsByToken,
        ImmutableDictionary<string, ImmutableArray<string>> tokensByPath,
        ImmutableDictionary<string, ImmutableHashSet<string>> childrenByPath,
        int missingParentCount)
    {
        _itemsByPath = itemsByPath;
        _pathsByToken = pathsByToken;
        _tokensByPath = tokensByPath;
        _childrenByPath = childrenByPath;
        _missingParentCount = missingParentCount;
    }

    public static SearchState Empty { get; } = new(
        ImmutableDictionary.Create<string, SearchItem>(PathComparer),
        ImmutableDictionary.Create<string, ImmutableHashSet<string>>(PathComparer),
        ImmutableDictionary.Create<string, ImmutableArray<string>>(PathComparer),
        ImmutableDictionary.Create<string, ImmutableHashSet<string>>(PathComparer),
        0);

    internal int MissingParentCount => _missingParentCount;

    internal ImmutableArray<string> TokensFor(string path) =>
        _tokensByPath.TryGetValue(path, out var tokens) ? tokens : ImmutableArray<string>.Empty;

    public int ItemCount => _itemsByPath.Count;

    public int TokenCount => _pathsByToken.Count;

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
        ITokenizer tokenizer) =>
        Create(nodes, tokenizer, shareTokens: true);

    internal static SearchState Create(
        IEnumerable<FileSystemNode> nodes,
        ITokenizer tokenizer,
        bool shareTokens)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(tokenizer);

        var sourceItems = ToDistinctItems(nodes);
        var items = ImmutableDictionary.CreateBuilder<string, SearchItem>(PathComparer);
        var pathBuildersByToken = new Dictionary<string, ImmutableHashSet<string>.Builder>(PathComparer);
        var canonicalTokens = shareTokens
            ? new Dictionary<string, string>(PathComparer)
            : null;
        var tokensByPath = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(PathComparer);

        foreach (var item in sourceItems)
        {
            var tokens = Tokenize(item.Name, tokenizer, canonicalTokens);
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
        var tokenListsByPath = new Dictionary<string, List<string>>(PathComparer);
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
                if (!tokenListsByPath.TryGetValue(node.FullPath, out var tokens))
                {
                    tokens = [];
                    tokenListsByPath[node.FullPath] = tokens;
                }

                if (!ContainsToken(tokens, token))
                {
                    tokens.Add(token);
                }
            }
        }

        var tokensByPath = ImmutableDictionary.CreateBuilder<string, ImmutableArray<string>>(PathComparer);
        foreach (var (path, tokens) in tokenListsByPath)
        {
            tokensByPath[path] = [.. tokens];
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

        var pathsToRemove = _missingParentCount == 0
            ? CollectSubtreeByWalk(normalizedPath)
            : CollectSubtreeByScan(normalizedPath);

        var state = this;
        foreach (var pathToRemove in pathsToRemove)
        {
            state = state.WithoutPath(pathToRemove);
        }

        return state;
    }

    private List<string> CollectSubtreeByWalk(string normalizedPath)
    {
        var pending = new Stack<string>();
        var visited = new HashSet<string>(PathComparer);
        var pathsToRemove = new List<string>();
        pending.Push(normalizedPath);
        while (pending.Count > 0)
        {
            var candidate = pending.Pop();
            if (!visited.Add(candidate))
            {
                continue;
            }

            if (_itemsByPath.ContainsKey(candidate))
            {
                pathsToRemove.Add(candidate);
            }

            foreach (var childPath in GetChildren(candidate))
            {
                pending.Push(childPath);
            }
        }

        pathsToRemove.Reverse();
        return pathsToRemove;
    }

    private List<string> CollectSubtreeByScan(string normalizedPath)
    {
        var endsWithSeparator =
            normalizedPath.EndsWith(Path.DirectorySeparatorChar) ||
            normalizedPath.EndsWith(Path.AltDirectorySeparatorChar);
        var descendantPrefix = endsWithSeparator
            ? normalizedPath
            : normalizedPath + Path.DirectorySeparatorChar;
        var alternateDescendantPrefix = endsWithSeparator
            ? null
            : normalizedPath + Path.AltDirectorySeparatorChar;

        return _itemsByPath.Keys
            .Where(candidate =>
                string.Equals(candidate, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase) ||
                (alternateDescendantPrefix != null &&
                 candidate.StartsWith(alternateDescendantPrefix, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(candidate => candidate.Length)
            .ToList();
    }

    private SearchState WithItem(
        SearchItem item,
        ImmutableArray<string> tokens)
    {
        var items = _itemsByPath.SetItem(item.FullPath, item);
        var pathsByToken = _pathsByToken;
        var canonicalTokens = tokens;
        string[]? rewritten = null;

        for (var index = 0; index < tokens.Length; index++)
        {
            var token = tokens[index];
            if (pathsByToken.TryGetKey(token, out var canonical) &&
                !ReferenceEquals(canonical, token))
            {
                if (rewritten == null)
                {
                    rewritten = new string[tokens.Length];
                    tokens.CopyTo(rewritten);
                }

                rewritten[index] = canonical;
                token = canonical;
            }

            pathsByToken.TryGetValue(token, out var paths);
            pathsByToken = pathsByToken.SetItem(
                token,
                (paths ?? ImmutableHashSet.Create<string>(PathComparer)).Add(item.FullPath));
        }

        if (rewritten != null)
        {
            canonicalTokens = ImmutableCollectionsMarshal.AsImmutableArray(rewritten);
        }

        var childrenByPath = _childrenByPath;
        if (item.ParentPath != null)
        {
            childrenByPath.TryGetValue(item.ParentPath, out var children);
            childrenByPath = childrenByPath.SetItem(
                item.ParentPath,
                (children ?? ImmutableHashSet.Create<string>(PathComparer)).Add(item.FullPath));
        }

        var missingParentCount = _missingParentCount;
        if (_itemsByPath.TryGetValue(item.FullPath, out var previous))
        {
            if (previous.ParentPath != null && !_itemsByPath.ContainsKey(previous.ParentPath))
            {
                missingParentCount--;
            }
        }
        else if (_childrenByPath.TryGetValue(item.FullPath, out var waiting))
        {
            missingParentCount -= waiting.Count;
        }

        if (item.ParentPath != null && !items.ContainsKey(item.ParentPath))
        {
            missingParentCount++;
        }

        return new SearchState(
            items,
            pathsByToken,
            _tokensByPath.SetItem(item.FullPath, canonicalTokens),
            childrenByPath,
            missingParentCount);
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

        var missingParentCount = _missingParentCount;
        if (item.ParentPath != null && !_itemsByPath.ContainsKey(item.ParentPath))
        {
            missingParentCount--;
        }

        if (_childrenByPath.TryGetValue(path, out var orphaned))
        {
            missingParentCount += orphaned.Count;
        }

        return new SearchState(
            _itemsByPath.Remove(path),
            pathsByToken,
            _tokensByPath.Remove(path),
            childrenByPath,
            missingParentCount);
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
        ImmutableDictionary<string, ImmutableArray<string>>.Builder tokensByPath)
    {
        var childrenByPath = BuildChildrenByPath(items.Values);
        var missingParentCount = 0;
        foreach (var (parentPath, children) in childrenByPath)
        {
            if (!items.ContainsKey(parentPath))
            {
                missingParentCount += children.Count;
            }
        }

        return new SearchState(
            items.ToImmutable(),
            pathsByToken.ToImmutable(),
            tokensByPath.ToImmutable(),
            childrenByPath,
            missingParentCount);
    }

    private static ImmutableDictionary<string, ImmutableHashSet<string>> BuildChildrenByPath(
        IEnumerable<SearchItem> items)
    {
        var childrenByPath = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        foreach (var item in items)
        {
            if (item.ParentPath == null)
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

    private static ImmutableArray<string> Tokenize(
        string value,
        ITokenizer tokenizer,
        Dictionary<string, string>? canonicalTokens = null)
    {
        var tokens = new List<string>();
        foreach (var token in tokenizer.Tokenize(value))
        {
            var canonical = token;
            if (canonicalTokens != null)
            {
                if (canonicalTokens.TryGetValue(token, out var existing))
                {
                    canonical = existing;
                }
                else
                {
                    canonicalTokens[token] = token;
                }
            }

            if (!ContainsToken(tokens, canonical))
            {
                tokens.Add(canonical);
            }
        }

        return [.. tokens];
    }

    private static bool ContainsToken(List<string> tokens, string token)
    {
        for (var index = 0; index < tokens.Count; index++)
        {
            if (PathComparer.Equals(tokens[index], token))
            {
                return true;
            }
        }

        return false;
    }
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
