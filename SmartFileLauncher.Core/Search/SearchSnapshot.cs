using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search;

public sealed class SearchSnapshot {
    public InvertedIndexSnapshot InvertedIndex { get; }
    public FileSystemNode? RootNode { get; }

    private SearchSnapshot(
        InvertedIndexSnapshot invertedIndex,
        FileSystemNode? rootNode) {
        InvertedIndex = invertedIndex;
        RootNode = rootNode;
    }

    public static SearchSnapshot Create(
        InvertedIndex invertedIndex,
        FileSystemNode? rootNode = null,
        CancellationToken cancellationToken = default) {
        ArgumentNullException.ThrowIfNull(invertedIndex);

        var indexSnapshot = invertedIndex.CreateSnapshot(cancellationToken);
        var nodes = indexSnapshot.GetAllNodes().ToList();
        if (rootNode != null) {
            AddTreeNodes(rootNode, nodes, cancellationToken);
        }

        return Create(indexSnapshot, nodes, rootNode, cancellationToken);
    }

    internal static SearchSnapshot Create(
        InvertedIndexSnapshot indexSnapshot,
        IEnumerable<FileSystemNode> sourceNodes,
        FileSystemNode? rootNode,
        CancellationToken cancellationToken = default) {
        var originalsByPath = new Dictionary<string, FileSystemNode>(
            StringComparer.OrdinalIgnoreCase);

        foreach (var node in sourceNodes.Concat(indexSnapshot.GetAllNodes())) {
            cancellationToken.ThrowIfCancellationRequested();
            originalsByPath.TryAdd(node.FullPath, node);
        }

        if (rootNode != null) {
            originalsByPath[rootNode.FullPath] = rootNode;
        }

        var clonesByPath = new Dictionary<string, FileSystemNode>(
            originalsByPath.Count,
            StringComparer.OrdinalIgnoreCase);
        foreach (var (path, node) in originalsByPath) {
            cancellationToken.ThrowIfCancellationRequested();
            clonesByPath[path] = CloneNode(node);
        }

        foreach (var (path, original) in originalsByPath) {
            cancellationToken.ThrowIfCancellationRequested();
            var parentPath = original.Parent?.FullPath;
            if (parentPath == null ||
                !clonesByPath.TryGetValue(parentPath, out var parentClone)) {
                continue;
            }

            parentClone.AddChild(clonesByPath[path]);
        }

        var rootClone = rootNode != null &&
                        clonesByPath.TryGetValue(rootNode.FullPath, out var mappedRoot)
            ? mappedRoot
            : null;

        return new SearchSnapshot(
            indexSnapshot.RemapNodes(clonesByPath, cancellationToken),
            rootClone);
    }

    private static void AddTreeNodes(
        FileSystemNode rootNode,
        ICollection<FileSystemNode> nodes,
        CancellationToken cancellationToken) {
        var pending = new Stack<FileSystemNode>();
        pending.Push(rootNode);

        while (pending.Count > 0) {
            cancellationToken.ThrowIfCancellationRequested();
            var node = pending.Pop();
            nodes.Add(node);

            foreach (var child in node.Children) {
                pending.Push(child);
            }
        }
    }

    private static FileSystemNode CloneNode(FileSystemNode node) =>
        new(node.Name, node.FullPath, node.IsDirectory) {
            Metadata = node.Metadata == null
                ? null
                : new FileMetadata {
                    SizeBytes = node.Metadata.SizeBytes,
                    CreatedTime = node.Metadata.CreatedTime,
                    LastWriteTime = node.Metadata.LastWriteTime,
                    OpenCount = node.Metadata.OpenCount
                }
        };
}
