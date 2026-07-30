using System.Collections.Generic;
namespace SmartFileLauncher.Core.Models;
/// <summary>
/// N-ary tree node representing a file or directory.
/// - Insert: O(1) to append child.
/// - Traversal (DFS/recursive build): O(N) over all nodes.
/// </summary>
public class FileSystemNode {
    private readonly object _childrenLock = new();
    private readonly List<FileSystemNode> _children = new();

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public FileSystemNode? Parent { get; private set; }
    public IReadOnlyList<FileSystemNode> Children {
        get {
            lock (_childrenLock) {
                return _children.ToArray();
            }
        }
    }
    // Extension point for future metadata (size, times, etc.)
    public FileMetadata? Metadata { get; set; }
    public FileSystemNode(string name, string fullPath, bool isDirectory) {
        Name = name; FullPath = fullPath; IsDirectory = isDirectory;
    }

    public void AddChild(FileSystemNode child) {
        ArgumentNullException.ThrowIfNull(child);

        lock (_childrenLock) {
            child.Parent = this;
            _children.Add(child);
        }
    }

    public bool RemoveChild(string fullPath) {
        lock (_childrenLock) {
            var removed = _children.RemoveAll(child =>
                string.Equals(child.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
            return removed > 0;
        }
    }
}
