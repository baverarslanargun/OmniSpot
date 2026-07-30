using System.Collections.Generic;
using System.Threading;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Utilities;
namespace SmartFileLauncher.Core.DataStructures;
/// <summary>
/// Inverted index: token -> list of nodes containing token.
/// - Add token: O(1) average (Dictionary + append).
/// - Remove token: O(n) where n = nodes in that token's list.
/// - Lookup k tokens: O(k + m) to gather raw matches (m total matched nodes before scoring).
/// - Fuzzy lookup: O(n*d) where n=indexed tokens, d=distance calculation
/// </summary>
public class InvertedIndex {
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private readonly Dictionary<string, List<FileSystemNode>> _index = new();
    
    // Reverse index: node path -> set of tokens (for fast removal)
    private readonly Dictionary<string, HashSet<string>> _nodeTokens =
        new(StringComparer.OrdinalIgnoreCase);
    
    public void Add(string token, FileSystemNode node) {
        _lock.EnterWriteLock();
        try {
            if (!_index.TryGetValue(token, out var list)) {
                list = new List<FileSystemNode>();
                _index[token] = list;
            }
            list.Add(node);

            // Track which tokens this node has
            if (!_nodeTokens.TryGetValue(node.FullPath, out var tokens)) {
                tokens = new HashSet<string>();
                _nodeTokens[node.FullPath] = tokens;
            }
            tokens.Add(token);
        } finally {
            _lock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Remove a node from all token lists. O(t * n) where t = tokens for this node.
    /// </summary>
    public void Remove(FileSystemNode node) {
        RemoveByPath(node.FullPath);
    }
    
    /// <summary>
    /// Remove a node by its path from all token lists.
    /// </summary>
    public void RemoveByPath(string path) {
        _lock.EnterWriteLock();
        try {
            if (!_nodeTokens.TryGetValue(path, out var tokens)) return;

            foreach (var token in tokens) {
                if (_index.TryGetValue(token, out var list)) {
                    list.RemoveAll(n =>
                        string.Equals(n.FullPath, path, StringComparison.OrdinalIgnoreCase));

                    // Clean up empty lists
                    if (list.Count == 0) {
                        _index.Remove(token);
                    }
                }
            }

            _nodeTokens.Remove(path);
        } finally {
            _lock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Remove a specific token-node association.
    /// </summary>
    public void Remove(string token, FileSystemNode node) {
        _lock.EnterWriteLock();
        try {
            if (_index.TryGetValue(token, out var list)) {
                list.RemoveAll(n =>
                    string.Equals(n.FullPath, node.FullPath, StringComparison.OrdinalIgnoreCase));
                if (list.Count == 0) {
                    _index.Remove(token);
                }
            }

            if (_nodeTokens.TryGetValue(node.FullPath, out var tokens)) {
                tokens.Remove(token);
                if (tokens.Count == 0) {
                    _nodeTokens.Remove(node.FullPath);
                }
            }
        } finally {
            _lock.ExitWriteLock();
        }
    }
    
    /// <summary>
    /// Check if a path is indexed.
    /// </summary>
    public bool Contains(string path) {
        _lock.EnterReadLock();
        try {
            return _nodeTokens.ContainsKey(path);
        } finally {
            _lock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Get the number of indexed nodes.
    /// </summary>
    public int NodeCount {
        get {
            _lock.EnterReadLock();
            try {
                return _nodeTokens.Count;
            } finally {
                _lock.ExitReadLock();
            }
        }
    }
    
    /// <summary>
    /// Get the number of unique tokens.
    /// </summary>
    public int TokenCount {
        get {
            _lock.EnterReadLock();
            try {
                return _index.Count;
            } finally {
                _lock.ExitReadLock();
            }
        }
    }
    
    /// <summary>
    /// Clear all index data.
    /// </summary>
    public void Clear() {
        _lock.EnterWriteLock();
        try {
            _index.Clear();
            _nodeTokens.Clear();
        } finally {
            _lock.ExitWriteLock();
        }
    }
    
    public IReadOnlyList<FileSystemNode> Get(string token) {
        _lock.EnterReadLock();
        try {
            return _index.TryGetValue(token, out var list)
                ? Array.AsReadOnly(list.ToArray())
                : Array.Empty<FileSystemNode>();
        } finally {
            _lock.ExitReadLock();
        }
    }
    
    /// <summary>
    /// Get nodes matching token with fuzzy matching (Levenshtein distance ≤ maxDistance)
    /// </summary>
    public IReadOnlyList<FileSystemNode> GetFuzzy(
        string token,
        int maxDistance = 2,
        CancellationToken cancellationToken = default) =>
        CreateSnapshot(cancellationToken)
            .GetFuzzy(token, maxDistance, cancellationToken);
    
    /// <summary>
    /// Get nodes where the indexed token contains the search token (substring match).
    /// Useful for finding "FR612" when searching for "612" or "FR".
    /// </summary>
    public IReadOnlyList<FileSystemNode> GetPartial(
        string token,
        CancellationToken cancellationToken = default) =>
        CreateSnapshot(cancellationToken)
            .GetPartial(token, cancellationToken);
    
    /// <summary>
    /// Get all indexed tokens (for debugging/diagnostics)
    /// </summary>
    public IEnumerable<string> GetAllTokens() {
        _lock.EnterReadLock();
        try {
            return _index.Keys.ToArray();
        } finally {
            _lock.ExitReadLock();
        }
    }

    public InvertedIndexSnapshot CreateSnapshot(
        CancellationToken cancellationToken = default) {
        _lock.EnterReadLock();
        try {
            var entries = new Dictionary<string, IReadOnlyList<FileSystemNode>>(_index.Count);
            foreach (var (token, nodes) in _index) {
                cancellationToken.ThrowIfCancellationRequested();
                entries[token] = Array.AsReadOnly(nodes.ToArray());
            }
            return new InvertedIndexSnapshot(entries);
        } finally {
            _lock.ExitReadLock();
        }
    }
}

public sealed class InvertedIndexSnapshot {
    private readonly IReadOnlyDictionary<string, IReadOnlyList<FileSystemNode>> _index;

    internal InvertedIndexSnapshot(
        IReadOnlyDictionary<string, IReadOnlyList<FileSystemNode>> index) {
        _index = index;
    }

    internal IReadOnlyDictionary<string, IReadOnlyList<FileSystemNode>> Entries => _index;

    public IReadOnlyList<FileSystemNode> Get(string token) =>
        _index.TryGetValue(token, out var nodes)
            ? nodes
            : Array.Empty<FileSystemNode>();

    public IReadOnlyList<FileSystemNode> GetFuzzy(
        string token,
        int maxDistance = 2,
        CancellationToken cancellationToken = default) {
        if (_index.TryGetValue(token, out var exactMatches)) {
            return exactMatches;
        }

        var matches = new HashSet<FileSystemNode>();
        foreach (var (indexedToken, nodes) in _index) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FuzzyMatcher.IsFuzzyMatch(token, indexedToken, maxDistance)) continue;

            foreach (var node in nodes) {
                matches.Add(node);
            }
        }

        return matches.ToArray();
    }

    public IReadOnlyList<FileSystemNode> GetPartial(
        string token,
        CancellationToken cancellationToken = default) {
        var matches = new HashSet<FileSystemNode>();
        foreach (var (indexedToken, nodes) in _index) {
            cancellationToken.ThrowIfCancellationRequested();
            if (!indexedToken.Contains(token)) continue;

            foreach (var node in nodes) {
                matches.Add(node);
            }
        }

        return matches.ToArray();
    }

    public IEnumerable<string> GetAllTokens() => _index.Keys.ToArray();

    internal IEnumerable<FileSystemNode> GetAllNodes() =>
        _index.Values.SelectMany(nodes => nodes).Distinct();

    internal InvertedIndexSnapshot RemapNodes(
        IReadOnlyDictionary<string, FileSystemNode> nodesByPath,
        CancellationToken cancellationToken = default) {
        var remappedEntries = new Dictionary<string, IReadOnlyList<FileSystemNode>>(_index.Count);

        foreach (var (token, nodes) in _index) {
            cancellationToken.ThrowIfCancellationRequested();
            var remappedNodes = nodes
                .Select(node => nodesByPath.TryGetValue(node.FullPath, out var mapped) ? mapped : null)
                .Where(node => node != null)
                .Cast<FileSystemNode>()
                .ToArray();
            remappedEntries[token] = Array.AsReadOnly(remappedNodes);
        }

        return new InvertedIndexSnapshot(remappedEntries);
    }
}
