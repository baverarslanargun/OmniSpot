using System.Collections.Generic;
namespace SmartFileLauncher.Core.Models;
/// <summary>
/// N-ary tree node representing a file or directory.
/// - Insert: O(1) to append child.
/// - Traversal (DFS/recursive build): O(N) over all nodes.
/// </summary>
public class FileSystemNode {
    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public FileSystemNode? Parent { get; private set; }
    public List<FileSystemNode> Children { get; } = new();
    // Extension point for future metadata (size, times, etc.)
    public FileMetadata? Metadata { get; set; }
    public FileSystemNode(string name, string fullPath, bool isDirectory) {
        Name = name; FullPath = fullPath; IsDirectory = isDirectory;
    }
    public void AddChild(FileSystemNode child) { child.Parent = this; Children.Add(child); }
}