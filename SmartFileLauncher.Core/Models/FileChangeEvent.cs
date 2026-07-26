namespace SmartFileLauncher.Core.Models;

/// <summary>
/// Represents a file system change event.
/// Used in the ConcurrentQueue event buffer.
/// </summary>
public class FileChangeEvent
{
    /// <summary>Type of change</summary>
    public FileChangeType ChangeType { get; set; }
    
    /// <summary>Full path of the affected file/directory</summary>
    public string FullPath { get; set; } = string.Empty;
    
    /// <summary>Old path (only for Renamed events)</summary>
    public string? OldPath { get; set; }
    
    /// <summary>Is this a directory? (false = file)</summary>
    public bool IsDirectory { get; set; }
    
    /// <summary>When the event was captured</summary>
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
    
    public override string ToString() =>
        ChangeType == FileChangeType.Renamed
            ? $"[{ChangeType}] {OldPath} → {FullPath}"
            : $"[{ChangeType}] {FullPath}";
}

/// <summary>
/// Types of file system changes
/// </summary>
public enum FileChangeType
{
    /// <summary>New file/directory created</summary>
    Created,
    
    /// <summary>File/directory deleted</summary>
    Deleted,
    
    /// <summary>File/directory renamed or moved</summary>
    Renamed,
    
    /// <summary>File content modified</summary>
    Modified
}
