using System.Collections.Generic;
using System.Linq;
using System.Collections.Concurrent;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.DataStructures;
namespace SmartFileLauncher.Core.Search;
/// <summary>
/// Search engine orchestrates tokenization, inverted index lookup, scoring.
/// Complexity: For k query tokens and m matched nodes total: O(k + m log m') where m' is number inserted into PQ (<= m).
/// PriorityQueue handles ordering in O(log m') per insertion.
/// </summary>
public class SearchEngine {
    private readonly InvertedIndex _invertedIndex;
    private readonly ITokenizer _tokenizer;
    private readonly IScoringStrategy _scoring;
    public SearchEngine(InvertedIndex invertedIndex, ITokenizer tokenizer, IScoringStrategy scoring) {
        _invertedIndex = invertedIndex; _tokenizer = tokenizer; _scoring = scoring;
    }
    public IEnumerable<SearchResult> Search(string query, int maxResults = 50) {
        var tokens = _tokenizer.Tokenize(query).ToArray();
        if (tokens.Length == 0) yield break;
        
        // Collect all candidate nodes with their token matches
        var nodeMatches = new Dictionary<string, (FileSystemNode node, HashSet<string> matchedTokens)>();
        
        foreach (var token in tokens) {
            foreach (var node in _invertedIndex.Get(token)) {
                if (!nodeMatches.ContainsKey(node.FullPath)) {
                    nodeMatches[node.FullPath] = (node, new HashSet<string>());
                }
                nodeMatches[node.FullPath].matchedTokens.Add(token);
            }
        }
        
        // Score based on how many tokens matched
        var pq = new PriorityQueue<SearchResult, double>();
        
        foreach (var (path, (node, matchedTokens)) in nodeMatches) {
            // Calculate score based on token match count and quality
            double score = 0;
            
            // Base score: number of matched tokens (prefer files matching more query words)
            int matchCount = matchedTokens.Count;
            score += matchCount * 50;
            
            // Bonus for matching all query tokens
            if (matchCount == tokens.Length) {
                score += 100;
            }
            
            // Check for exact filename match
            bool exactMatch = string.Equals(node.Name, query, StringComparison.OrdinalIgnoreCase);
            if (exactMatch) {
                score += 200;
            }
            
            // Bonus for exact token matches
            var fileTokens = _tokenizer.Tokenize(node.Name).ToHashSet();
            foreach (var token in matchedTokens) {
                if (fileTokens.Contains(token)) {
                    score += 25;
                }
            }
            
            // Frequency bonus from metadata
            var freq = node.Metadata?.OpenCount ?? 0;
            score += freq * 2;
            
            pq.Enqueue(new SearchResult { Name = node.Name, FullPath = node.FullPath, Score = score }, -score);
        }
        
        int count = 0;
        while (pq.Count > 0 && count < maxResults) {
            yield return pq.Dequeue();
            count++;
        }
    }
}