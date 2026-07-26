namespace SmartFileLauncher.Core.Models;

/// <summary>
/// Represents a file entry in the SQLite index database.
/// Maps to the 'Files' table.
/// </summary>
public class IndexedFile
{
    /// <summary>Internal database ID (auto-increment)</summary>
    public long Id { get; set; }
    
    /// <summary>Full path to the file (unique key)</summary>
    public string FullPath { get; set; } = string.Empty;
    
    /// <summary>File name with extension</summary>
    public string FileName { get; set; } = string.Empty;
    
    /// <summary>File extension (lowercase, with dot)</summary>
    public string Extension { get; set; } = string.Empty;
    
    /// <summary>Parent directory ID (FK to Directories table)</summary>
    public long DirectoryId { get; set; }
    
    /// <summary>File size in bytes</summary>
    public long SizeBytes { get; set; }
    
    /// <summary>File creation time (UTC ticks)</summary>
    public long CreatedTimeUtc { get; set; }
    
    /// <summary>Last write time (UTC ticks) - used for delta sync</summary>
    public long LastWriteTimeUtc { get; set; }
    
    /// <summary>When this file was last indexed (UTC ticks)</summary>
    public long LastIndexedTimeUtc { get; set; }
    
    /// <summary>Number of times this file was opened (frequency scoring)</summary>
    public int OpenCount { get; set; }
    
    /// <summary>Is file hidden?</summary>
    public bool IsHidden { get; set; }
    
    /// <summary>Is system file?</summary>
    public bool IsSystem { get; set; }
    
    // Helper properties for DateTime conversion
    public DateTime CreatedTime => new DateTime(CreatedTimeUtc, DateTimeKind.Utc).ToLocalTime();
    public DateTime LastWriteTime => new DateTime(LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime();
    public DateTime LastIndexedTime => new DateTime(LastIndexedTimeUtc, DateTimeKind.Utc).ToLocalTime();
}
