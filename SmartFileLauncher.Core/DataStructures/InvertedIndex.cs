using System.Collections.Generic;
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
    private readonly Dictionary<string, List<FileSystemNode>> _index = new();
    
    // Reverse index: node path -> set of tokens (for fast removal)
    private readonly Dictionary<string, HashSet<string>> _nodeTokens = new();
    
    public void Add(string token, FileSystemNode node) {
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
        if (!_nodeTokens.TryGetValue(path, out var tokens)) return;
        
        foreach (var token in tokens) {
            if (_index.TryGetValue(token, out var list)) {
                list.RemoveAll(n => n.FullPath == path);
                
                // Clean up empty lists
                if (list.Count == 0) {
                    _index.Remove(token);
                }
            }
        }
        
        _nodeTokens.Remove(path);
    }
    
    /// <summary>
    /// Remove a specific token-node association.
    /// </summary>
    public void Remove(string token, FileSystemNode node) {
        if (_index.TryGetValue(token, out var list)) {
            list.RemoveAll(n => n.FullPath == node.FullPath);
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
    }
    
    /// <summary>
    /// Check if a path is indexed.
    /// </summary>
    public bool Contains(string path) => _nodeTokens.ContainsKey(path);
    
    /// <summary>
    /// Get the number of indexed nodes.
    /// </summary>
    public int NodeCount => _nodeTokens.Count;
    
    /// <summary>
    /// Get the number of unique tokens.
    /// </summary>
    public int TokenCount => _index.Count;
    
    /// <summary>
    /// Clear all index data.
    /// </summary>
    public void Clear() {
        _index.Clear();
        _nodeTokens.Clear();
    }
    
    public IReadOnlyList<FileSystemNode> Get(string token) => _index.TryGetValue(token, out var list) ? list : new List<FileSystemNode>();
    
    /// <summary>
    /// Get nodes matching token with fuzzy matching (Levenshtein distance ≤ maxDistance)
    /// </summary>
    public IReadOnlyList<FileSystemNode> GetFuzzy(string token, int maxDistance = 2) {
        // First try exact match
        if (_index.TryGetValue(token, out var exactList)) return exactList;
        
        // If no exact match, try fuzzy matching against all indexed tokens
        var fuzzyMatches = new HashSet<FileSystemNode>();
        foreach (var indexedToken in _index.Keys) {
            if (FuzzyMatcher.IsFuzzyMatch(token, indexedToken, maxDistance)) {
                foreach (var node in _index[indexedToken]) {
                    fuzzyMatches.Add(node);
                }
            }
        }
        
        return fuzzyMatches.ToList();
    }
    
    /// <summary>
    /// Get nodes where the indexed token contains the search token (substring match).
    /// Useful for finding "FR612" when searching for "612" or "FR".
    /// </summary>
    public IReadOnlyList<FileSystemNode> GetPartial(string token) {
        var matches = new HashSet<FileSystemNode>();
        foreach (var indexedToken in _index.Keys) {
            if (indexedToken.Contains(token)) {
                foreach (var node in _index[indexedToken]) {
                    matches.Add(node);
                }
            }
        }
        return matches.ToList();
    }
    
    /// <summary>
    /// Get all indexed tokens (for debugging/diagnostics)
    /// </summary>
    public IEnumerable<string> GetAllTokens() => _index.Keys;
}