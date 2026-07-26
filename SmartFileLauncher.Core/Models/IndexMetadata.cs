namespace SmartFileLauncher.Core.Models;

/// <summary>
/// Stores metadata about the index itself.
/// Maps to the 'Metadata' table (key-value store).
/// </summary>
public class IndexMetadata
{
    /// <summary>Metadata key</summary>
    public string Key { get; set; } = string.Empty;
    
    /// <summary>Metadata value (stored as string, parse as needed)</summary>
    public string Value { get; set; } = string.Empty;
    
    // Common metadata keys
    public static class Keys
    {
        /// <summary>Schema version for migrations</summary>
        public const string SchemaVersion = "schema_version";
        
        /// <summary>Last full scan completion time (UTC ticks)</summary>
        public const string LastFullScanTime = "last_full_scan_time";
        
        /// <summary>Root path that was scanned</summary>
        public const string ScanRootPath = "scan_root_path";
        
        /// <summary>Total files indexed</summary>
        public const string TotalFilesIndexed = "total_files_indexed";
        
        /// <summary>Total directories indexed</summary>
        public const string TotalDirectoriesIndexed = "total_directories_indexed";
        
        /// <summary>Index build duration in milliseconds</summary>
        public const string LastBuildDurationMs = "last_build_duration_ms";
    }
}
