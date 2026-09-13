using SmartFileLauncher.Core.Indexing.Ntfs;

namespace SmartFileLauncher.Core.Search;

internal sealed partial class LiveCatalog
{
    internal int ReserveDirectory()
    {
        _gate.EnterWriteLock();
        try { CheckWritable(); return NewNode(0, null, true); }
        finally { _gate.ExitWriteLock(); }
    }
    internal int AddMft(NtfsMftEntry entry, int directoryId, int parentId)
    {
        _gate.EnterWriteLock();
        try
        {
            CheckWritable();
            var id = entry.IsDirectory ? directoryId : FindNode(parentId, entry.Name);
            if (id == 0) id = NewNode(parentId, entry.Name, false);
            else if (entry.IsDirectory && id != 1 && _nodes.Int32(Row(id) + 4) == 0)
            {
                for (var current = parentId; current != 0; current = Parent(current))
                    if (current == id) throw new InvalidDataException("MFT ebeveyn döngüsü.");
                _nodes.PutInt32(Row(id), parentId | int.MinValue | LateName);
                _nodes.PutInt32(Row(id) + 4, AddName(entry.Name));
                LinkChild(parentId, id); InsertNodeHash(id, Hash(parentId, entry.Name));
                if (IsConnected(parentId)) ConnectSubtree(id);
            }
            var name = id == 1 ? Name(id)! : entry.Name;
            if (Arrived(id, _stamp))
            {
                var old = ReadMetadata(id);
                if (old.ModifiedUtc != entry.LastWriteTimeUtc || old.CreatedUtc != entry.CreatedTimeUtc ||
                    old.Size != (entry.IsDirectory ? null : entry.SizeBytes) || Name(id) != name)
                    throw new InvalidDataException("MFT aynı ad için çelişen kayıt.");
                return id;
            }
            var item = new SearchItem(name, "", entry.IsDirectory, entry.IsDirectory ? null : entry.SizeBytes,
                entry.IsDirectory ? null : new DateTime(entry.CreatedTimeUtc, DateTimeKind.Utc).ToLocalTime(),
                entry.IsDirectory ? null : new DateTime(entry.LastWriteTimeUtc, DateTimeKind.Utc).ToLocalTime(), 0, null);
            Fill(id, new(item, entry.LastWriteTimeUtc, entry.CreatedTimeUtc,
                (entry.Attributes & FileAttributes.Hidden) != 0, (entry.Attributes & FileAttributes.System) != 0, _indexedUtc), false);
            BeforePublish?.Invoke(); Publish(); return id;
        }
        catch { _poisoned = true; throw; }
        finally { _gate.ExitWriteLock(); }
    }
}

internal sealed class LiveMftIngestor
{
    private readonly LiveCatalog _catalog;
    private readonly ulong _rootReference;
    private readonly Dictionary<ulong, int> _directories;
    internal LiveMftIngestor(LiveCatalog catalog, ulong rootReference)
    { _catalog = catalog; _rootReference = rootReference; _directories = new() { [rootReference] = 1 }; }
    private int Directory(ulong reference)
    {
        if (_directories.TryGetValue(reference, out var id)) return id;
        id = _catalog.ReserveDirectory(); _directories.Add(reference, id); return id;
    }
    internal int Accept(NtfsMftEntry entry)
    {
        var parent = entry.IsDirectory && entry.FileReference == _rootReference ? 0 : Directory(entry.ParentFileReference);
        return _catalog.AddMft(entry, entry.IsDirectory ? Directory(entry.FileReference) : 0, parent);
    }
    internal int Consume(NtfsMftReader reader, CancellationToken cancellationToken = default)
    {
        var count = 0;
        foreach (var entry in reader.ReadEntries(cancellationToken)) { Accept(entry); count++; }
        return count;
    }
}
