using System.Collections.Generic;
namespace SmartFileLauncher.Core.Models;
public class FileSystemNode {
    private List<FileSystemNode>? _children;
    private Func<IReadOnlyList<FileSystemNode>>? _childrenFactory;

    public string Name { get; }
    public string FullPath { get; }
    public bool IsDirectory { get; }
    public FileSystemNode? Parent { get; private set; }
    public IReadOnlyList<FileSystemNode> Children {
        get {
            var children = GetChildrenList();
            if (children == null) return Array.Empty<FileSystemNode>();

            lock (children) {
                return children.ToArray();
            }
        }
    }
    public FileMetadata? Metadata { get; set; }
    public FileSystemNode(string name, string fullPath, bool isDirectory) {
        Name = name; FullPath = fullPath; IsDirectory = isDirectory;
    }

    internal FileSystemNode(string name, string fullPath, bool isDirectory,
        FileSystemNode? parent, Func<IReadOnlyList<FileSystemNode>>? childrenFactory)
        : this(name, fullPath, isDirectory) {
        Parent = parent;
        _childrenFactory = childrenFactory;
    }

    private List<FileSystemNode>? GetChildrenList() {
        var factory = Volatile.Read(ref _childrenFactory);
        var children = Volatile.Read(ref _children);
        if (children == null && factory != null) {
            var created = factory().ToList();
            children = Interlocked.CompareExchange(ref _children, created, null) ?? created;
            Interlocked.Exchange(ref _childrenFactory, null);
        }
        return children;
    }

    public void AddChild(FileSystemNode child) {
        ArgumentNullException.ThrowIfNull(child);

        var children = GetChildrenList() ?? LazyInitializer.EnsureInitialized(ref _children);
        lock (children) {
            child.Parent = this;
            children.Add(child);
        }
    }

    public bool RemoveChild(string fullPath) {
        var children = GetChildrenList();
        if (children == null) return false;

        lock (children) {
            var removed = children.RemoveAll(child =>
                string.Equals(child.FullPath, fullPath, StringComparison.OrdinalIgnoreCase));
            return removed > 0;
        }
    }
}
