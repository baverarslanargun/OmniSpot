namespace SmartFileLauncher.Core.Models;

public class IndexedFile
{
    public long Id { get; set; }
    
    public string FullPath { get; set; } = string.Empty;
    
    public string FileName { get; set; } = string.Empty;
    
    public string Extension { get; set; } = string.Empty;
    
    public long DirectoryId { get; set; }
    
    public long SizeBytes { get; set; }
    
    public long CreatedTimeUtc { get; set; }
    
    public long LastWriteTimeUtc { get; set; }
    
    public long LastIndexedTimeUtc { get; set; }
    
    public int OpenCount { get; set; }
    
    public bool IsHidden { get; set; }
    
    public bool IsSystem { get; set; }
    
    public DateTime CreatedTime => new DateTime(CreatedTimeUtc, DateTimeKind.Utc).ToLocalTime();
    public DateTime LastWriteTime => new DateTime(LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime();
    public DateTime LastIndexedTime => new DateTime(LastIndexedTimeUtc, DateTimeKind.Utc).ToLocalTime();
}
