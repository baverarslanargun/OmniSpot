namespace SmartFileLauncher.Core.Models;

public class IndexedDirectory
{
    public long Id { get; set; }
    
    public string FullPath { get; set; } = string.Empty;
    
    public string Name { get; set; } = string.Empty;
    
    public long? ParentId { get; set; }
    
    public int Depth { get; set; }
    
    public long LastWriteTimeUtc { get; set; }
    
    public long LastIndexedTimeUtc { get; set; }
    
    public bool IsHidden { get; set; }
    
    public DateTime LastWriteTime => new DateTime(LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime();
    public DateTime LastIndexedTime => new DateTime(LastIndexedTimeUtc, DateTimeKind.Utc).ToLocalTime();
}
