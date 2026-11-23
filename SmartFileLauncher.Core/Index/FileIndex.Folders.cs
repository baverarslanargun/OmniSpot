namespace SmartFileLauncher.Core.Index;

internal readonly record struct IndexFolderChild(ulong Reference, string Name, bool IsDirectory, long SizeBytes,
    uint Modified, uint Created, byte Attributes);

internal sealed record IndexFolderListing(ulong Reference, IReadOnlyList<IndexFolderChild> Children);

public sealed partial class FileIndex
{
    private const int DirectChildScanLimit = 16;

    internal ulong MaxReference()
    {
        _lock.EnterReadLock();
        try
        {
            ulong max = 0;
            for (var row = 0; row < _count; row++)
            {
                var reference = _frn[row] & RecordMask;
                if (reference > max) max = reference;
            }
            return max;
        }
        finally { _lock.ExitReadLock(); }
    }

    internal Dictionary<string, IndexFolderListing> ListFolders(IEnumerable<string> paths, CancellationToken cancellationToken)
    {
        _lock.EnterReadLock();
        try
        {
            var result = new Dictionary<string, IndexFolderListing>(StringComparer.OrdinalIgnoreCase);
            var folders = new Dictionary<uint, string>();
            foreach (var path in paths)
            {
                var row = FindPath(path);
                if (row == NoRow || !IsAlive(row) || !IsDirectory(row)) continue;
                folders.TryAdd(row, path);
            }
            if (folders.Count == 0) return result;
            var children = folders.Keys.ToDictionary(row => row, _ => new List<IndexFolderChild>());
            if (folders.Count <= DirectChildScanLimit)
            {
                foreach (var folder in folders.Keys)
                    foreach (var child in ChildRows(folder, cancellationToken)) children[folder].Add(FolderChild(child));
            }
            else
            {
                for (var row = 0u; row < _count; row++)
                {
                    if ((row & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                    if (row == _root || !IsAlive(row) || !children.TryGetValue(_parent[row], out var list)) continue;
                    list.Add(FolderChild(row));
                }
            }
            foreach (var (row, path) in folders) result[path] = new(_frn[row], children[row]);
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    private IndexFolderChild FolderChild(uint row) =>
        new(_frn[row], _names.Text(_name[row]), IsDirectory(row), _size[row], _modified[row], _created[row],
            (byte)(_attributes[row] & AttributeMask));

    internal static bool SameMetadata(in IndexFolderChild child, in IndexEntry entry) =>
        child.IsDirectory == entry.IsDirectory &&
        string.Equals(child.Name, entry.Name, StringComparison.Ordinal) &&
        child.SizeBytes == (entry.IsDirectory ? 0 : entry.SizeBytes) &&
        child.Modified == Seconds(entry.LastWriteTimeUtc) &&
        child.Created == Seconds(entry.CreatedTimeUtc) &&
        child.Attributes == Pack(entry.Attributes);
}
