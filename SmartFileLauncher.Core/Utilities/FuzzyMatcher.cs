namespace SmartFileLauncher.Core.Utilities;

public static class FuzzyMatcher {
    public static bool IsFuzzyMatch(string source, string target, int maxDistance = 2) {
        if (string.IsNullOrEmpty(source) || string.IsNullOrEmpty(target)) return false;
        
        if (Math.Abs(source.Length - target.Length) > maxDistance) return false;
        
        int distance = LevenshteinDistance(source, target);
        return distance <= maxDistance;
    }
    
    public static int LevenshteinDistance(string source, string target) {
        int m = source.Length;
        int n = target.Length;
        
        if (m == 0) return n;
        if (n == 0) return m;
        
        int[,] dp = new int[m + 1, n + 1];
        
        for (int i = 0; i <= m; i++) dp[i, 0] = i;
        for (int j = 0; j <= n; j++) dp[0, j] = j;
        
        for (int i = 1; i <= m; i++) {
            for (int j = 1; j <= n; j++) {
                int cost = (source[i - 1] == target[j - 1]) ? 0 : 1;
                
                dp[i, j] = Math.Min(
                    Math.Min(
                        dp[i - 1, j] + 1,
                        dp[i, j - 1] + 1),
                    dp[i - 1, j - 1] + cost
                );
            }
        }
        
        return dp[m, n];
    }
    
    public static List<string> FindFuzzyMatches(string query, IEnumerable<string> candidates, int maxDistance = 2) {
        var matches = new List<(string candidate, int distance)>();
        
        foreach (var candidate in candidates) {
            int distance = LevenshteinDistance(query.ToLowerInvariant(), candidate.ToLowerInvariant());
            if (distance <= maxDistance) {
                matches.Add((candidate, distance));
            }
        }
        
        return matches.OrderBy(m => m.distance).Select(m => m.candidate).ToList();
    }
}
