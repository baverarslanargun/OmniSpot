using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager
{
    private bool UsesCompactCatalog => _layout == SearchStateLayout.Compact;
    private bool _compactRootAvailable;
    private string? _compactSingleRootPath;
    private static int _postStartupGcRequested;

    private void ReleaseCompactStartupWorkspace()
    {
        if (!UsesCompactCatalog || CurrentSearchState.ItemCount < 100_000 ||
            Interlocked.CompareExchange(ref _postStartupGcRequested, 1, 0) != 0)
            return;

        GC.Collect(GC.MaxGeneration, GCCollectionMode.Forced, blocking: false, compacting: false);
    }

    private static FileMetadata CreateCompactMetadata(SearchItem item) => new()
    {
        SizeBytes = item.SizeBytes,
        CreatedTime = item.CreatedTime,
        LastWriteTime = item.LastWriteTime,
        OpenCount = item.OpenCount
    };

    private CatalogNodeProjection CreateCompactNodeProjection() => new(
        (IIndexCatalogSnapshot)CurrentSearchState, _compactRootAvailable, _compactSingleRootPath);

    internal IReadOnlyList<FileSystemNode> GetIndexedRootNodes()
    {
        lock (_lock)
        {
            return UsesCompactCatalog
                ? CreateCompactNodeProjection().Root?.Children ?? Array.Empty<FileSystemNode>()
                : _rootNode?.Children ?? Array.Empty<FileSystemNode>();
        }
    }

    private List<FileSystemNode> CollectCompactSubtreeNodes(string path)
    {
        var projection = CreateCompactNodeProjection();
        var start = projection.GetNode(path);
        var result = new List<FileSystemNode>();
        if (start is null) return result;
        var pending = new Stack<FileSystemNode>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        pending.Push(start);
        while (pending.TryPop(out var node))
        {
            if (!visited.Add(node.FullPath)) continue;
            result.Add(node);
            foreach (var child in node.Children) pending.Push(child);
        }
        Interlocked.Add(ref _subtreeNodesInspected, result.Count);
        return result;
    }

    private sealed class CatalogNodeProjection
    {
        private readonly IIndexCatalogSnapshot _snapshot;
        private readonly Dictionary<string, FileSystemNode> _nodes = new(StringComparer.OrdinalIgnoreCase);
        private readonly HashSet<string> _constructing = new(StringComparer.OrdinalIgnoreCase);
        private readonly object _gate = new();
        internal FileSystemNode? Root { get; }

        internal CatalogNodeProjection(IIndexCatalogSnapshot snapshot, bool available, string? singleRoot)
        {
            _snapshot = snapshot;
            if (!available) return;
            if (singleRoot is null)
                Root = new FileSystemNode("Root", "", true, null, GetRoots);
            else
            {
                var name = Path.GetFileName(singleRoot);
                Root = new FileSystemNode(string.IsNullOrEmpty(name) ? singleRoot : name,
                    singleRoot, true, null, () => GetChildren(singleRoot));
                _nodes.Add(singleRoot, Root);
            }
        }

        internal IReadOnlyDictionary<string, FileSystemNode> Nodes
        {
            get
            {
                lock (_gate)
                {
                    foreach (var item in _snapshot.GetAllItems()) GetNode(item.FullPath);
                    return _nodes;
                }
            }
        }

        internal FileSystemNode? GetNode(string path)
        {
            lock (_gate)
            {
                if (_nodes.TryGetValue(path, out var existing)) return existing;
                if (!_snapshot.TryGetItem(path, out var item) || !_constructing.Add(path)) return null;
                try
                {
                    var parent = !string.IsNullOrEmpty(item.ParentPath)
                        ? GetNode(item.ParentPath)
                        : item.ParentPath is not null ? Root : null;
                    var node = new FileSystemNode(item.Name, item.FullPath, item.IsDirectory,
                        parent, item.IsDirectory ? () => GetChildren(item.FullPath) : null)
                    {
                        Metadata = item.IsDirectory ? null : CreateCompactMetadata(item)
                    };
                    _nodes.Add(item.FullPath, node);
                    return node;
                }
                finally { _constructing.Remove(path); }
            }
        }

        private IReadOnlyList<FileSystemNode> GetRoots() => _snapshot.GetRoots()
            .Where(item => item.ParentPath is not null || item.IsDirectory)
            .Select(item => GetNode(item.FullPath)!).ToArray();

        private IReadOnlyList<FileSystemNode> GetChildren(string path) => _snapshot.GetChildren(path)
            .Select(item => GetNode(item.FullPath)!).ToArray();
    }
}
