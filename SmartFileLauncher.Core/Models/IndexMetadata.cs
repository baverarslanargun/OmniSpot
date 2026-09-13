namespace SmartFileLauncher.Core.Models;

public class IndexMetadata
{
    public string Key { get; set; } = string.Empty;
    
    public string Value { get; set; } = string.Empty;
    
    public static class Keys
    {
        public const string SchemaVersion = "schema_version";
        
        public const string LastFullScanTime = "last_full_scan_time";
        
        public const string ScanRootPath = "scan_root_path";
        
        public const string TotalFilesIndexed = "total_files_indexed";
        
        public const string TotalDirectoriesIndexed = "total_directories_indexed";
        
        public const string LastBuildDurationMs = "last_build_duration_ms";
        public const string InitialInventoryPending = "initial_inventory_pending";
        public const string LastBootstrapSource = "last_bootstrap_source";
        public const string LastBootstrapLinkScopes = "last_bootstrap_link_scopes";
    }
}
