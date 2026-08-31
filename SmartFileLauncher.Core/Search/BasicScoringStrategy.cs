using SmartFileLauncher.Core.Models;
namespace SmartFileLauncher.Core.Search;
public class BasicScoringStrategy : IScoringStrategy {
    public double Score(string queryToken, FileSystemNode node, bool exactMatch) {
        double baseScore = exactMatch ? 100.0 : 25.0;
        var freq = node.Metadata?.OpenCount ?? 0;
        return baseScore + freq * 2;
    }
}