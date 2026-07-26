using SmartFileLauncher.Core.Models;
namespace SmartFileLauncher.Core.Search;
/// <summary>
/// Simple scoring: exact match high, contains lower + frequency bonus.
/// TODO: Extend with TF-IDF, recency decay, etc.
/// </summary>
public class BasicScoringStrategy : IScoringStrategy {
    public double Score(string queryToken, FileSystemNode node, bool exactMatch) {
        double baseScore = exactMatch ? 100.0 : 25.0;
        var freq = node.Metadata?.OpenCount ?? 0; // small boost
        return baseScore + freq * 2; // naive additive model
    }
}