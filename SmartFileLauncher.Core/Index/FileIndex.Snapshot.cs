using System.Text;
using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.Core.Index;

public sealed partial class FileIndex
{
    private const ulong SnapshotMagic = 0x3158444E49494D4FUL;
    private const int SnapshotVersion = 3;
    private const int UnsortedSnapshotVersion = 2;

    internal void CompactNow(CancellationToken cancellationToken = default)
    {
        _lock.EnterWriteLock();
        try { Compact(cancellationToken); }
        finally
        {
            _lock.ExitWriteLock();
            ReleaseIfCompacted();
        }
    }

    private void ReleaseIfCompacted()
    {
        if (!_compacted) return;
        _compacted = false;
        IndexMemory.Release();
    }

    private void Compact(CancellationToken cancellationToken)
    {
        var map = new uint[_count];
        var names = new IndexNames();
        var alive = _count - _dead;
        var frn = new ulong[alive + 1024];
        var parent = new uint[alive + 1024];
        var name = new int[alive + 1024];
        var attributes = new uint[alive + 1024];
        var size = new long[alive + 1024];
        var modified = new long[alive + 1024];
        var created = new uint[alive + 1024];
        var next = 0u;
        for (var row = 0u; row < _count; row++)
        {
            if (!IsAlive(row)) { map[row] = NoRow; continue; }
            if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            map[row] = next;
            frn[next] = _frn[row];
            parent[next] = _parent[row];
            name[next] = names.Intern(_names.Text(_name[row]));
            attributes[next] = _attributes[row];
            size[next] = _size[row];
            modified[next] = _modified[row];
            created[next] = _created[row];
            next++;
        }
        for (var row = 0u; row < next; row++) parent[row] = parent[row] == NoRow ? NoRow : map[parent[row]];
        _names = names;
        _frn = frn;
        _parent = parent;
        _name = name;
        _attributes = attributes;
        _size = size;
        _modified = modified;
        _created = created;
        _words = null;
        _generation++;
        _root = map[_root];
        _count = (int)next;
        _dead = 0;
        RebuildRecordLinks();
        RebuildOrders(cancellationToken);
        Trim();
        _compacted = true;
    }

    private void RebuildRecordLinks()
    {
        var records = 0;
        for (var row = 0u; row < _count; row++)
            if (IsAlive(row)) records = Math.Max(records, RecordOf(_frn[row]) + 1);
        _rowByRecord = new int[records + Math.Max(1024, records / 64)];
        _nextLink.Clear();
        for (var row = 0u; row < _count; row++)
            if (IsAlive(row)) LinkRecord(row);
    }

    public void Save(string path, IndexCheckpoint checkpoint) => Save(path, checkpoint, withSortOrders: true);

    internal void Save(string path, IndexCheckpoint checkpoint, bool withSortOrders)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        var directory = Path.GetDirectoryName(Path.GetFullPath(path));
        if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
        var temporary = path + ".tmp";
        _lock.EnterReadLock();
        try
        {
            using (var file = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None, 1 << 20))
            {
                var checksum = new ChecksumStream(file);
                using (var writer = new BinaryWriter(checksum, Encoding.UTF8, leaveOpen: true))
                {
                    writer.Write(SnapshotMagic);
                    writer.Write(withSortOrders ? SnapshotVersion : UnsortedSnapshotVersion);
                    writer.Write(RootPath);
                    writer.Write(RootReference);
                    writer.Write(checkpoint.VolumeSerial);
                    writer.Write(checkpoint.JournalId);
                    writer.Write(checkpoint.NextUsn);
                    writer.Write(_count);
                    writer.Write(_dead);
                    writer.Write(_root);
                    _names.Write(writer);
                    IndexArrays.Write(writer, _frn.AsSpan(0, _count));
                    IndexArrays.Write(writer, _parent.AsSpan(0, _count));
                    IndexArrays.Write(writer, _name.AsSpan(0, _count));
                    IndexArrays.Write(writer, _attributes.AsSpan(0, _count));
                    IndexArrays.Write(writer, _size.AsSpan(0, _count));
                    IndexArrays.Write(writer, _modified.AsSpan(0, _count));
                    IndexArrays.Write(writer, _created.AsSpan(0, _count));
                    writer.Write(_byName.Count);
                    IndexArrays.Write(writer, _byName.Rows.AsSpan(0, _byName.Count));
                    if (withSortOrders)
                    {
                        WriteOrder(writer, EnsureOrder(SortField.Size, CancellationToken.None));
                        WriteOrder(writer, EnsureOrder(SortField.Modified, CancellationToken.None));
                    }
                }
                Span<byte> hash = stackalloc byte[8];
                BitConverter.TryWriteBytes(hash, checksum.Hash);
                file.Write(hash);
                file.Flush(true);
            }
            File.Move(temporary, path, overwrite: true);
        }
        finally { _lock.ExitReadLock(); }
    }

    public static FileIndex? Load(string path, out IndexCheckpoint checkpoint)
    {
        checkpoint = default;
        if (!File.Exists(path)) return null;
        try
        {
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 1 << 20);
            if (file.Length < 16) return null;
            var checksum = new ChecksumStream(file);
            using var reader = new BinaryReader(checksum, Encoding.UTF8, leaveOpen: true);
            if (reader.ReadUInt64() != SnapshotMagic) return null;
            var version = reader.ReadInt32();
            if (version is not (SnapshotVersion or UnsortedSnapshotVersion)) return null;
            var index = new FileIndex(reader.ReadString(), reader.ReadUInt64());
            checkpoint = new IndexCheckpoint(reader.ReadUInt64(), reader.ReadUInt64(), reader.ReadInt64());
            var count = reader.ReadInt32();
            var dead = reader.ReadInt32();
            var root = reader.ReadUInt32();
            if (count <= 0 || dead < 0 || dead >= count || root >= count) return null;
            index._names = IndexNames.Read(reader);
            var capacity = count + Math.Max(1024, count / 64);
            index._frn = IndexArrays.Read<ulong>(reader, count, capacity);
            index._parent = IndexArrays.Read<uint>(reader, count, capacity);
            index._name = IndexArrays.Read<int>(reader, count, capacity);
            index._attributes = IndexArrays.Read<uint>(reader, count, capacity);
            index._size = IndexArrays.Read<long>(reader, count, capacity);
            index._modified = IndexArrays.Read<long>(reader, count, capacity);
            index._created = IndexArrays.Read<uint>(reader, count, capacity);
            var ordered = reader.ReadInt32();
            if (ordered != count - dead) return null;
            var byName = IndexArrays.Read<uint>(reader, ordered, ordered + Math.Max(1024, ordered / 64));
            var bySize = version == SnapshotVersion ? ReadOrder(reader, ordered) : null;
            var byModified = version == SnapshotVersion ? ReadOrder(reader, ordered) : null;
            if (version == SnapshotVersion && (bySize is null || byModified is null)) return null;
            var expected = checksum.Hash;
            Span<byte> stored = stackalloc byte[8];
            if (file.ReadAtLeast(stored, 8, throwOnEndOfStream: false) != 8 || BitConverter.ToUInt64(stored) != expected ||
                file.Position != file.Length) return null;
            index._count = count;
            index._dead = dead;
            index._root = root;
            var files = 0;
            for (var row = 0; row < count; row++)
            {
                var parent = index._parent[row];
                if (index._name[row] < 0 || index._name[row] >= index._names.Count ||
                    parent != NoRow && parent >= count ||
                    index.IsAlive((uint)row) && (parent == NoRow) != (row == root)) return null;
                if (index.IsAlive((uint)row) && !index.IsDirectory((uint)row)) files++;
            }
            for (var i = 0; i < ordered; i++)
                if (byName[i] >= count || !index.IsAlive(byName[i])) return null;
            if (bySize is not null && !index.IsFileOrder(bySize, files) ||
                byModified is not null && !index.IsFileOrder(byModified, files)) return null;
            index._byName = new RowOrder(byName, ordered);
            index.RebuildRecordLinks();
            index._bySize = bySize ?? index.BuildOrder(SortField.Size, CancellationToken.None);
            index._byModified = byModified ?? index.BuildOrder(SortField.Modified, CancellationToken.None);
            return index;
        }
        catch (Exception failure) when (failure is IOException or InvalidDataException or EndOfStreamException or ArgumentException)
        {
            checkpoint = default;
            return null;
        }
    }

    private static void WriteOrder(BinaryWriter writer, RowOrder order)
    {
        writer.Write(order.Count);
        IndexArrays.Write(writer, order.Rows.AsSpan(0, order.Count));
    }

    private static RowOrder? ReadOrder(BinaryReader reader, int limit)
    {
        var count = reader.ReadInt32();
        if (count < 0 || count > limit) return null;
        return new RowOrder(IndexArrays.Read<uint>(reader, count, count + Math.Max(1024, count / 64)), count);
    }

    private bool IsFileOrder(RowOrder order, int files)
    {
        if (order.Count != files) return false;
        for (var i = 0; i < order.Count; i++)
        {
            var row = order.Rows[i];
            if (row >= _count || !IsAlive(row) || IsDirectory(row)) return false;
        }
        return true;
    }
}
