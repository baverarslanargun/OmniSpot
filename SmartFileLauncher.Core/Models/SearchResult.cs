namespace SmartFileLauncher.Core.Models;
/// <summary>
/// Result model returned to UI.
/// </summary>
public class SearchResult {
    public string Name { get; init; } = string.Empty;
    public string FullPath { get; init; } = string.Empty;
    public double Score { get; init; }
    public bool IsDirectory { get; init; }
}
