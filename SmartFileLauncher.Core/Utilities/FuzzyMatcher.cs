namespace SmartFileLauncher.Core.Utilities;

/// <summary>
/// Provides fuzzy string matching using Levenshtein distance (edit distance)
/// </summary>
public static class FuzzyMatcher {
    /// <summary>
    /// Check if two strings are similar within a threshold (default: 2 edits)
    /// </summary>
    public static bool IsFuzzyMatch(string source, string target, int maxDistance = 2) {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return false;
        
        // Quick check: if length difference is greater than maxDistance, can't match
        if (Math.Abs(source.Length - target.Length) > maxDistance) return false;
        
        int distance = LevenshteinDistance(source, target);
        return distance <= maxDistance;
    }
    
    /// <summary>
    /// Calculate Levenshtein distance (minimum number of single-character edits)
    /// Time complexity: O(m*n) where m,n are string lengths
    /// Space complexity: O(m*n) - can be optimized to O(n) with rolling array
    /// </summary>
    public static int LevenshteinDistance(string source, string target) {
        int m = source.Length;
        int n = target.Length;
        
        // Edge cases
        if (m == 0) return n;
        if (n == 0) return m;
        
        // Create distance matrix
        int[,] dp = new int[m + 1, n + 1];
        
        // Initialize base cases
        for (int i = 0; i <= m; i++) dp[i, 0] = i;  // Delete all chars from source
        for (int j = 0; j <= n; j++) dp[0, j] = j;  // Insert all chars to source
        
        // Fill matrix using dynamic programming
        for (int i = 1; i <= m; i++) {
            for (int j = 1; j <= n; j++) {
                int cost = (source[i - 1] == target[j - 1]) ? 0 : 1;
                
                dp[i, j] = Math.Min(
                    Math.Min(
                        dp[i - 1, j] + 1,      // Delete from source
                        dp[i, j - 1] + 1),     // Insert to source
                    dp[i - 1, j - 1] + cost    // Replace in source
                );
            }
        }
        
        return dp[m, n];
    }
    
    /// <summary>
    /// Find best fuzzy matches from a list of candidates
    /// </summary>
    public static List<string> FindFuzzyMatches(string query, IEnumerable<string> candidates, int maxDistance = 2) {
        var matches = new List<(string candidate, int distance)>();
        
        foreach (var candidate in candidates) {
            int distance = LevenshteinDistance(query.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance <= maxDistance) {
                matches.Add((candidate, distance));
            }
        }
        
        // Sort by distance (closer matches first)
        return matches.OrderBy(m => m.distance).Select(m => m.candidate).ToList();
    }
}
