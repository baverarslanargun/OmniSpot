namespace SmartFileLauncher.Core.Models;

public class FileChangeEvent
{
    public FileChangeType ChangeType { get; set; }
    
    public string FullPath { get; set; } = string.Empty;
    
    public string? OldPath { get; set; }
    
    public bool IsDirectory { get; set; }
    
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    public override string ToString() =>
        ChangeType == FileChangeType.Renamed
            ? $"[{ChangeType}] {OldPath} → {FullPath}"
            : $"[{ChangeType}] {FullPath}";
}

public enum FileChangeType
{
    Created,
    
    Deleted,
    
    Renamed,
    
    Modified
}
