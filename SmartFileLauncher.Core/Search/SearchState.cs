using System.Collections.Immutable;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search;

public sealed class SearchState
{
    private static readonly StringComparer PathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly ImmutableDictionary<string, SearchItem> _itemsByPath;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _pathsByToken;
    private readonly ImmutableDictionary<string, ImmutableHashSet<string>> _tokensByPath;

    private SearchState(
        ImmutableDictionary<string, SearchItem> itemsByPath,
        ImmutableDictionary<string, ImmutableHashSet<string>> pathsByToken,
        ImmutableDictionary<string, ImmutableHashSet<string>> tokensByPath)
    {
        _itemsByPath = itemsByPath;
        _pathsByToken = pathsByToken;
        _tokensByPath = tokensByPath;
    }

    public static SearchState Empty { get; } = new(
        ImmutableDictionary.Create<string, SearchItem>(PathComparer),
        ImmutableDictionary.Create<string, ImmutableHashSet<string>>(PathComparer),
        ImmutableDictionary.Create<string, ImmutableHashSet<string>>(PathComparer));

    public int ItemCount => _itemsByPath.Count;

    public IReadOnlyCollection<SearchItem> Get(string token)
    {
        if (!_pathsByToken.TryGetValue(token, out var paths))
        {
            return Array.Empty<SearchItem>();
        }

        var items = new List<SearchItem>(paths.Count);
        foreach (var path in paths)
        {
            if (_itemsByPath.TryGetValue(path, out var item))
            {
                items.Add(item);
            }
        }

        return items;
    }

    public static SearchState Create(
        IEnumerable<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(tokenizer);

        var items = ImmutableDictionary.CreateBuilder<string, SearchItem>(PathComparer);
        var pathsByToken = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);
        var tokensByPath = ImmutableDictionary.CreateBuilder<string, ImmutableHashSet<string>>(PathComparer);

        foreach (var node in nodes)
        {
            var item = SearchItem.FromNode(node);
            var tokens = Tokenize(item.Name, tokenizer);
            items[item.FullPath] = item;
            tokensByPath[item.FullPath] = tokens;

            foreach (var token in tokens)
            {
                pathsByToken.TryGetValue(token, out var paths);
                pathsByToken[token] = (paths ?? ImmutableHashSet.Create<string>(PathComparer))
                    .Add(item.FullPath);
            }
        }

        return new SearchState(
            items.ToImmutable(),
            pathsByToken.ToImmutable(),
            tokensByPath.ToImmutable());
    }

    public SearchState WithUpserts(
        IEnumerable<FileSystemNode> nodes,
        ITokenizer tokenizer)
    {
        ArgumentNullException.ThrowIfNull(nodes);
        ArgumentNullException.ThrowIfNull(tokenizer);

        var state = this;
        foreach (var node in nodes)
        {
            var item = SearchItem.FromNode(node);
            state = state.WithUpsert(item, Tokenize(item.Name, tokenizer));
        }

        return state;
    }

    public SearchState WithoutPathAndDescendants(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        var normalizedPath = path.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        var descendantPrefix = normalizedPath + Path.DirectorySeparatorChar;
        var alternateDescendantPrefix = normalizedPath + Path.AltDirectorySeparatorChar;

        var pathsToRemove = _itemsByPath.Keys
            .Where(candidate =>
                string.Equals(candidate, normalizedPath, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(descendantPrefix, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(alternateDescendantPrefix, StringComparison.OrdinalIgnoreCase))
            .ToArray();

        var state = this;
        foreach (var pathToRemove in pathsToRemove)
        {
            state = state.WithoutPath(pathToRemove);
        }

        return state;
    }

    private SearchState WithUpsert(
        SearchItem item,
        ImmutableHashSet<string> tokens)
    {
        var state = WithoutPath(item.FullPath);
        var items = state._itemsByPath.SetItem(item.FullPath, item);
        var pathsByToken = state._pathsByToken;

        foreach (var token in tokens)
        {
            pathsByToken.TryGetValue(token, out var paths);
            pathsByToken = pathsByToken.SetItem(
                token,
                (paths ?? ImmutableHashSet.Create<string>(PathComparer)).Add(item.FullPath));
        }

        return new SearchState(
            items,
            pathsByToken,
            state._tokensByPath.SetItem(item.FullPath, tokens));
    }

    private SearchState WithoutPath(string path)
    {
        if (!_itemsByPath.ContainsKey(path))
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

        return new SearchState(
            _itemsByPath.Remove(path),
            pathsByToken,
            _tokensByPath.Remove(path));
    }

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
    int OpenCount)
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
            metadata?.OpenCount ?? 0);
    }
}
