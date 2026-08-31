using System;
namespace SmartFileLauncher.Core.Models;
public class FileMetadata {
    public long? SizeBytes { get; set; }
    public DateTime? CreatedTime { get; set; }
    public DateTime? LastWriteTime { get; set; }
    public int OpenCount { get; set; }
}