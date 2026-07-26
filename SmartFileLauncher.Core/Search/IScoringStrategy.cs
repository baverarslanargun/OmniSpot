using SmartFileLauncher.Core.Models;
namespace SmartFileLauncher.Core.Search;
public interface IScoringStrategy {
    double Score(string queryToken, FileSystemNode node, bool exactMatch);
}