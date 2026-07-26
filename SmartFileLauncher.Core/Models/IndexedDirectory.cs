namespace SmartFileLauncher.Core.Models;

/// <summary>
/// Represents a directory entry in the SQLite index database.
/// Maps to the 'Directories' table.
/// </summary>
public class IndexedDirectory
{
    /// <summary>Internal database ID (auto-increment)</summary>
    public long Id { get; set; }
    
    /// <summary>Full path to the directory (unique key)</summary>
    public string FullPath { get; set; } = string.Empty;
    
    /// <summary>Directory name</summary>
    public string Name { get; set; } = string.Empty;
    
    /// <summary>Parent directory ID (null for root, FK to self)</summary>
    public long? ParentId { get; set; }
    
    /// <summary>Depth level from scan root (root = 0)</summary>
    public int Depth { get; set; }
    
    /// <summary>Last write time (UTC ticks)</summary>
    public long LastWriteTimeUtc { get; set; }
    
    /// <summary>When this directory was last indexed (UTC ticks)</summary>
    public long LastIndexedTimeUtc { get; set; }
    
    /// <summary>Is directory hidden?</summary>
    public bool IsHidden { get; set; }
    
    // Helper properties for DateTime conversion
    public DateTime LastWriteTime => new DateTime(LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime();
    public DateTime LastIndexedTime => new DateTime(LastIndexedTimeUtc, DateTimeKind.Utc).ToLocalTime();
}
