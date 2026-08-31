namespace SmartFileLauncher.Core.Models;
public class SearchResult {
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public double Score { get; init; }
    public bool IsDirectory { get; init; }
}
