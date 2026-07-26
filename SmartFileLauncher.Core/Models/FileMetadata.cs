using System;
namespace SmartFileLauncher.Core.Models;
/// <summary>
/// Basic metadata captured for each file.
/// </summary>
public class FileMetadata {
    public long? SizeBytes { get; set; }
    public DateTime? CreatedTime { get; set; }
    public DateTime? LastWriteTime { get; set; }
    public int OpenCount { get; set; } // simple frequency counter
}