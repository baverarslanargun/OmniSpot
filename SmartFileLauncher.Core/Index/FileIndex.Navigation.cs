using System.Text;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

public sealed partial class FileIndex
{
    public (int Files, int Directories) Counts()
    {
        _lock.EnterReadLock();
        try
        {
            var files = 0;
            var directories = 0;
            for (var row = 0u; row < _count; row++)
            {
                if (!IsAlive(row) || row == _root) continue;
                if (IsDirectory(row)) directories++; else files++;
            }
            return (files, directories);
        }
        finally { _lock.ExitReadLock(); }
    }

    internal SearchItem RootItem()
    {
        _lock.EnterReadLock();
        try { return ToItem(_root); }
        finally { _lock.ExitReadLock(); }
    }

    internal bool TryGetItem(string path, out SearchItem item)
    {
        _lock.EnterReadLock();
        try
        {
            var row = FindPath(path);
            item = row == NoRow ? null! : ToItem(row);
            return row != NoRow;
        }
        finally { _lock.ExitReadLock(); }
    }

    internal IReadOnlyList<SearchItem> Children(string path, CancellationToken cancellationToken)
    {
        _lock.EnterReadLock();
        try
        {
            var folder = FindPath(path);
            if (folder == NoRow || !IsDirectory(folder)) return [];
            var children = ChildRows(folder, cancellationToken);
            children.Sort(CompareByName);
            return children.Select(ToItem).ToArray();
        }
        finally { _lock.ExitReadLock(); }
    }

    internal IReadOnlyList<SearchItem> Descendants(string path, CancellationToken cancellationToken)
    {
        _lock.EnterReadLock();
        try
        {
            var start = FindPath(path);
            if (start == NoRow || !IsDirectory(start)) return [];
            var result = new List<SearchItem>();
            var pending = new Stack<uint>();
            pending.Push(start);
            while (pending.TryPop(out var folder))
                foreach (var child in ChildRows(folder, cancellationToken))
                {
                    result.Add(ToItem(child));
                    if (IsDirectory(child)) pending.Push(child);
                }
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    internal IReadOnlyList<SearchItem> AllItems(CancellationToken cancellationToken)
    {
        _lock.EnterReadLock();
        try
        {
            var result = new List<SearchItem>(_byName.Count);
            for (var i = 0; i < _byName.Count; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (_byName.Rows[i] != _root) result.Add(ToItem(_byName.Rows[i]));
            }
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    private List<uint> ChildRows(uint folder, CancellationToken cancellationToken)
    {
        var children = new List<uint>();
        var parents = _parent.AsSpan(0, _count);
        for (var at = 0; at < parents.Length;)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var found = parents[at..].IndexOf(folder);
            if (found < 0) break;
            var child = (uint)(at + found);
            if (IsAlive(child) && child != _root) children.Add(child);
            at += found + 1;
        }
        return children;
    }

    private uint FindPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return NoRow;
        var full = Path.TrimEndingDirectorySeparator(path);
        var root = Path.TrimEndingDirectorySeparator(RootPath);
        if (full.Equals(root, StringComparison.OrdinalIgnoreCase)) return _root;
        if (!full.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return NoRow;
        var at = _root;
        foreach (var part in full[(root.Length + 1)..].Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries))
        {
            at = FindChild(at, part);
            if (at == NoRow) return NoRow;
        }
        return at;
    }

    private uint FindChild(uint parent, string name)
    {
        var target = Encoding.UTF8.GetBytes(name.ToUpperInvariant());
        var lo = 0;
        var hi = _byName.Count;
        while (lo < hi)
        {
            var mid = (lo + hi) >>> 1;
            if (_names.Folded(_name[_byName.Rows[mid]]).SequenceCompareTo(target) < 0) lo = mid + 1; else hi = mid;
        }
        var fallback = NoRow;
        for (var i = lo; i < _byName.Count; i++)
        {
            var row = _byName.Rows[i];
            if (!_names.Folded(_name[row]).SequenceEqual(target)) break;
            if (_parent[row] != parent) continue;
            if (string.Equals(_names.Text(_name[row]), name, StringComparison.Ordinal)) return row;
            if (fallback == NoRow) fallback = row;
        }
        return fallback;
    }

    private bool InScope(uint row, HashSet<uint>? scope)
    {
        if (scope is null) return true;
        for (var at = row; at != NoRow; at = _parent[at])
        {
            if (scope.Contains(at)) return true;
            if (at == _root) return false;
        }
        return false;
    }

    private HashSet<uint>? ResolveScope(IReadOnlyList<string>? scope)
    {
        if (scope is null || scope.Count == 0) return null;
        var rows = new HashSet<uint>();
        foreach (var path in scope)
        {
            var row = FindPath(path);
            if (row == _root) return null;
            if (row != NoRow) rows.Add(row);
        }
        return rows;
    }

    private SearchItem ToItem(uint row)
    {
        var directory = IsDirectory(row);
        return new SearchItem(
            NameOf(row),
            PathOf(row),
            directory,
            directory ? null : _size[row],
            CreatedOf(row),
            ModifiedOf(row),
            0,
            row == _root ? "" : PathOf(_parent[row]));
    }
}
