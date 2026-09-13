using System.Buffers.Binary;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;

namespace SmartFileLauncher.Core.Search;

internal sealed class CompactCatalog : CatalogReader
{
    private const int Magic = 0x4F534349;
    private const int HeaderSize = 56;
    private const int RowSize = 64;
    private readonly CatalogBuffer _buffer;
    private readonly Dictionary<string, int> _terms = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, List<int>> _orphanChildren = new(StringComparer.OrdinalIgnoreCase);
    private readonly long[] _pathKeys;
    private readonly int[] _roots;
    private readonly int _rows;
    private readonly int _termRows;
    private readonly int _children;
    private readonly int _itemTokens;
    private readonly int _rowSize;
    private readonly int _flagsOffset;
    private readonly int _parentGroupCount;
    private readonly bool _varint;

    internal override int ItemCount { get; }
    internal override int TokenCount => _terms.Count;
    internal override int MissingParentCount { get; }
    internal override int PayloadBytes { get; }
    internal override bool UsesVarint => _varint;
    internal override long Generation { get; }
    internal override IEnumerable<string> Tokens => _terms.Keys;
    internal override bool ContainsToken(string token) => _terms.ContainsKey(token);

    private CompactCatalog(CatalogBuffer buffer, IReadOnlyList<SearchItem>? sourceItems = null)
    {
        _buffer = buffer;
        var version = buffer.ReadInt32(4);
        if (buffer.ReadInt32(0) != Magic || version is not (2 or 3))
            throw new InvalidDataException("Kompakt katalog sürümü geçersiz.");
        _rowSize = version == 2 ? 80 : RowSize;
        _flagsOffset = version == 2 ? 60 : 28;
        ItemCount = buffer.ReadInt32(8);
        var tokenCount = buffer.ReadInt32(12);
        _rows = buffer.ReadInt32(16);
        _termRows = buffer.ReadInt32(20);
        _children = buffer.ReadInt32(24);
        _itemTokens = buffer.ReadInt32(28);
        PayloadBytes = buffer.ReadInt32(40);
        _varint = buffer.ReadInt32(44) == 1;
        Generation = buffer.ReadInt64(48);
        if (ItemCount < 0 || tokenCount < 0 || PayloadBytes != buffer.Length || _rows != HeaderSize ||
            (long)_rows + (long)ItemCount * _rowSize != _termRows ||
            (long)_termRows + (long)tokenCount * 16 != _children ||
            _children > _itemTokens || _itemTokens > buffer.ReadInt32(32) ||
            buffer.ReadInt32(32) > buffer.ReadInt32(36) || buffer.ReadInt32(36) > PayloadBytes ||
            buffer.ReadInt32(44) is < 0 or > 1)
            throw new InvalidDataException("Kompakt katalog bölüm sınırları geçersiz.");
        if (version == 3)
        {
            if ((long)_children + 4 > _itemTokens)
                throw new InvalidDataException("Kompakt alt öğe tablosu geçersiz.");
            _parentGroupCount = buffer.ReadInt32(_children);
            if (_parentGroupCount < 0 || _parentGroupCount > ItemCount ||
                (long)_children + 4 + (long)_parentGroupCount * 12 > _itemTokens)
                throw new InvalidDataException("Kompakt alt öğe tablosu geçersiz.");
        }
        _pathKeys = new long[ItemCount];
        var roots = new List<int>();
        var pathBuffer = sourceItems is null ? new char[256] : Array.Empty<char>();
        for (var id = 0; id < ItemCount; id++)
        {
            _pathKeys[id] = sourceItems is null ? ReadPathKey(id, ref pathBuffer) : PathKey(sourceItems[id].FullPath, id);
            var row = Row(id);
            if (buffer.ReadInt32(row + 20) < 0 && buffer.ReadInt32(row + 24) >= 0)
            {
                var parent = ReadExternalParent(row);
                if (parent.Length == 0) roots.Add(id);
                else MissingParentCount++;
                if (!_orphanChildren.TryGetValue(parent, out var children))
                    _orphanChildren[parent] = children = [];
                children.Add(id);
            }
            else if (buffer.ReadInt32(row + 20) < 0)
            {
                roots.Add(id);
            }
        }
        _roots = roots.ToArray();
        Array.Sort(_pathKeys);
        for (var term = 0; term < tokenCount; term++)
            _terms.Add(ReadTerm(term), term);
    }

    internal static CompactCatalog Create(IReadOnlyList<SearchItem> items, ITokenizer tokenizer, bool varint, long generation = 0)
        => Create(items, item => tokenizer.Tokenize(item.Name), varint, generation);

    internal static CompactCatalog Create(IReadOnlyList<SearchItem> items,
        Func<SearchItem, IEnumerable<string>> tokenize, bool varint, long generation = 0)
    {
        using var payload = new MemoryStream();
        Serialize(payload, items, tokenize, varint, generation);
        return new CompactCatalog(new ArrayBuffer(payload.ToArray()), items);
    }

    internal static CompactCatalog CreateMapped(string path, IReadOnlyList<SearchItem> items,
        Func<SearchItem, IEnumerable<string>> tokenize, bool varint, long generation)
    {
        using (var payload = new MemoryStream())
        {
            Serialize(payload, items, tokenize, varint, generation);
            WritePayload(path, payload.GetBuffer(), checked((int)payload.Length));
        }
        return OpenMapped(path, verify: false);
    }

    private static void WritePayload(string path, byte[] payload, int length)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        stream.Write(payload, 0, length);
        hash.AppendData(payload, 0, length);
        stream.Write(hash.GetHashAndReset());
        stream.Flush(flushToDisk: true);
    }

    private static void Serialize(Stream stream, IReadOnlyList<SearchItem> items,
        Func<SearchItem, IEnumerable<string>> tokenize, bool varint, long generation)
    {
        var paths = new Dictionary<string, int>(items.Count, StringComparer.OrdinalIgnoreCase);
        for (var id = 0; id < items.Count; id++) paths.Add(items[id].FullPath, id);
        var tokens = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var tokenNames = new List<string>();
        var postings = new List<List<int>>();
        var itemTokens = new List<int>[items.Count];
        var children = new List<int>[items.Count];
        var parents = new int[items.Count];
        long textBytes = 0, postingBytes = 0, tokenLinks = 0, childLinks = 0;
        Array.Fill(parents, -1);
        for (var id = 0; id < items.Count; id++) children[id] = [];
        for (var id = 0; id < items.Count; id++)
        {
            itemTokens[id] = [];
            foreach (var token in tokenize(items[id]).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                if (!tokens.TryGetValue(token, out var term))
                {
                    term = tokens.Count;
                    tokens.Add(token, term);
                    tokenNames.Add(token);
                    textBytes += (long)token.Length * 2;
                    postings.Add([]);
                }
                itemTokens[id].Add(term);
                var previous = postings[term].Count == 0 ? 0 : postings[term][^1];
                postingBytes += varint
                    ? System.Numerics.BitOperations.Log2((uint)(id - previous)) / 7 + 1
                    : 4;
                tokenLinks++;
                postings[term].Add(id);
            }
            if (items[id].ParentPath is { } parent && paths.TryGetValue(parent, out var parentId))
            {
                parents[id] = parentId;
                children[parentId].Add(id);
                childLinks++;
            }
            var item = items[id];
            var prefixId = FindPrefix(item.FullPath, parents[id]);
            var fragment = item.FullPath.AsSpan(prefixId < 0 ? 0 : items[prefixId].FullPath.Length);
            textBytes += (long)fragment.Length * 2;
            if (!fragment.EndsWith(item.Name.AsSpan(), StringComparison.Ordinal))
                textBytes += (long)item.Name.Length * 2;
            if ((parents[id] < 0 || !string.Equals(item.ParentPath, items[parents[id]].FullPath, StringComparison.Ordinal)) &&
                item.ParentPath is { } externalParent)
                textBytes += 4 + (long)externalParent.Length * 2;
        }
        using var writer = new BinaryWriter(stream, System.Text.Encoding.Unicode, leaveOpen: true);
        var termOffset = checked(HeaderSize + items.Count * RowSize);
        var childOffset = checked(termOffset + tokens.Count * 16);
        var parentGroups = Enumerable.Range(0, items.Count).Where(id => children[id].Count != 0).ToArray();
        var childDataOffset = checked(childOffset + 4 + parentGroups.Length * 12);
        if (stream is MemoryStream memory)
            memory.Capacity = checked((int)(childDataOffset + childLinks * 4 + tokenLinks * 4 + postingBytes + textBytes));
        stream.SetLength(childDataOffset);
        stream.Position = childDataOffset;
        var childStarts = new int[items.Count];
        var tokenStarts = new int[items.Count];
        for (var id = 0; id < items.Count; id++)
        {
            childStarts[id] = checked((int)stream.Position);
            foreach (var child in children[id]) writer.Write(child);
        }
        var itemTokenOffset = checked((int)stream.Position);
        for (var id = 0; id < items.Count; id++)
        {
            tokenStarts[id] = checked((int)stream.Position);
            foreach (var term in itemTokens[id]) writer.Write(term);
        }
        var postingOffset = checked((int)stream.Position);
        var postingStarts = new int[tokens.Count];
        for (var term = 0; term < tokens.Count; term++)
        {
            postingStarts[term] = checked((int)stream.Position);
            var previous = 0;
            foreach (var id in postings[term])
            {
                if (varint)
                {
                    var delta = (uint)(id - previous);
                    while (delta >= 128)
                    {
                        writer.Write((byte)(delta | 128));
                        delta >>= 7;
                    }
                    writer.Write((byte)delta);
                    previous = id;
                }
                else writer.Write(id);
            }
        }
        var arenaOffset = checked((int)stream.Position);
        for (var id = 0; id < items.Count; id++)
        {
            var item = items[id];
            var prefixId = FindPrefix(item.FullPath, parents[id]);
            var fragment = item.FullPath.AsSpan(prefixId < 0 ? 0 : items[prefixId].FullPath.Length);
            var fragmentOffset = AppendString(fragment);
            var nameOffset = fragment.EndsWith(item.Name.AsSpan(), StringComparison.Ordinal)
                ? fragmentOffset + (fragment.Length - item.Name.Length) * 2 : AppendString(item.Name);
            var externalParent = parents[id] < 0 || !string.Equals(item.ParentPath, items[parents[id]].FullPath, StringComparison.Ordinal)
                ? item.ParentPath : null;
            var parentOffset = -1;
            if (externalParent is not null)
            {
                parentOffset = checked((int)stream.Position);
                writer.Write(externalParent.Length);
                AppendString(externalParent);
            }
            var end = stream.Position;
            stream.Position = HeaderSize + id * RowSize;
            writer.Write(nameOffset); writer.Write(item.Name.Length);
            writer.Write(fragmentOffset); writer.Write(fragment.Length);
            writer.Write(prefixId); writer.Write(parents[id]);
            writer.Write(parentOffset);
            var flags = (item.IsDirectory ? 1 : 0) | (item.SizeBytes.HasValue ? 2 : 0) |
                (item.CreatedTime.HasValue ? 4 | ((int)item.CreatedTime.Value.Kind << 4) : 0) |
                (item.LastWriteTime.HasValue ? 8 | ((int)item.LastWriteTime.Value.Kind << 6) : 0);
            writer.Write(flags);
            writer.Write(item.SizeBytes ?? 0);
            writer.Write(item.CreatedTime?.Ticks ?? 0);
            writer.Write(item.LastWriteTime?.Ticks ?? 0);
            writer.Write(item.OpenCount);
            writer.Write(tokenStarts[id]);
            stream.Position = end;
        }
        for (var term = 0; term < tokens.Count; term++)
        {
            var offset = AppendString(tokenNames[term]);
            var end = stream.Position;
            stream.Position = termOffset + term * 16;
            writer.Write(offset); writer.Write(tokenNames[term].Length);
            writer.Write(postingStarts[term]); writer.Write(postings[term].Count);
            stream.Position = end;
        }
        var length = checked((int)stream.Length);
        stream.Position = childOffset;
        writer.Write(parentGroups.Length);
        foreach (var id in parentGroups)
        {
            writer.Write(id);
            writer.Write(childStarts[id]);
            writer.Write(children[id].Count);
        }
        stream.Position = 0;
        foreach (var value in new[] { Magic, 3, items.Count, tokens.Count, HeaderSize, termOffset,
                     childOffset, itemTokenOffset, postingOffset, arenaOffset, length, varint ? 1 : 0 })
            writer.Write(value);
        writer.Write(generation);
        writer.Flush();

        int FindPrefix(string path, int parentId)
        {
            if (parentId < 0) return -1;
            var prefix = items[parentId].FullPath;
            return path.Length > prefix.Length && path[prefix.Length] == '\\' &&
                path.AsSpan().StartsWith(prefix.AsSpan(), StringComparison.Ordinal) ? parentId : -1;
        }

        int AppendString(ReadOnlySpan<char> value)
        {
            var offset = checked((int)stream.Position);
            foreach (var character in value) writer.Write((ushort)character);
            return offset;
        }
    }

    internal override SearchItem GetItem(int id, Dictionary<int, string>? pathCache = null)
    {
        var row = Row(id);
        var flags = _buffer.ReadInt32(row + _flagsOffset);
        var parentId = _buffer.ReadInt32(row + 20);
        var externalParent = _buffer.ReadInt32(row + 24);
        return new(_buffer.ReadString(_buffer.ReadInt32(row), _buffer.ReadInt32(row + 4)), GetPath(id, pathCache),
            (flags & 1) != 0, (flags & 2) != 0 ? _buffer.ReadInt64(row + 32) : null,
            (flags & 4) != 0 ? new DateTime(_buffer.ReadInt64(row + 40), (DateTimeKind)((flags >> 4) & 3)) : null,
            (flags & 8) != 0 ? new DateTime(_buffer.ReadInt64(row + 48), (DateTimeKind)((flags >> 6) & 3)) : null,
            _buffer.ReadInt32(row + 56), externalParent >= 0 ?
                ReadExternalParent(row) : parentId >= 0 ? GetPath(parentId, pathCache) : null);
    }

    internal override SearchItem GetTransientItem(int id, Dictionary<int, string> sharedPrefixPaths)
    {
        var row = Row(id);
        var flags = _buffer.ReadInt32(row + _flagsOffset);
        var prefixId = _buffer.ReadInt32(row + 16);
        var parentId = _buffer.ReadInt32(row + 20);
        var externalParent = _buffer.ReadInt32(row + 24);
        var fragment = _buffer.ReadString(_buffer.ReadInt32(row + 8), _buffer.ReadInt32(row + 12));
        var fullPath = prefixId >= 0 ? GetPath(prefixId, sharedPrefixPaths) + fragment : fragment;
        return new(
            _buffer.ReadString(_buffer.ReadInt32(row), _buffer.ReadInt32(row + 4)),
            fullPath,
            (flags & 1) != 0,
            (flags & 2) != 0 ? _buffer.ReadInt64(row + 32) : null,
            (flags & 4) != 0
                ? new DateTime(_buffer.ReadInt64(row + 40), (DateTimeKind)((flags >> 4) & 3))
                : null,
            (flags & 8) != 0
                ? new DateTime(_buffer.ReadInt64(row + 48), (DateTimeKind)((flags >> 6) & 3))
                : null,
            _buffer.ReadInt32(row + 56),
            externalParent >= 0
                ? ReadExternalParent(row)
                : parentId >= 0 ? GetPath(parentId, sharedPrefixPaths) : null);
    }

    internal override bool Matches(int id, QueryCatalogFilter filter)
    {
        var row = Row(id);
        var flags = _buffer.ReadInt32(row + _flagsOffset);
        return filter.Matches(
            isDirectory: (flags & 1) != 0,
            sizeBytes: (flags & 2) != 0 ? _buffer.ReadInt64(row + 32) : null,
            createdTime: (flags & 4) != 0
                ? new DateTime(_buffer.ReadInt64(row + 40), (DateTimeKind)((flags >> 4) & 3))
                : null,
            lastWriteTime: (flags & 8) != 0
                ? new DateTime(_buffer.ReadInt64(row + 48), (DateTimeKind)((flags >> 6) & 3))
                : null);
    }

    internal override int Find(string path, Dictionary<int, string>? pathCache = null)
    {
        var key = PathKey(path, 0);
        var low = 0;
        var high = _pathKeys.Length;
        while (low < high)
        {
            var middle = low + (high - low) / 2;
            if (_pathKeys[middle] < key) low = middle + 1;
            else high = middle;
        }
        for (; low < _pathKeys.Length && (_pathKeys[low] >> 32) == (key >> 32); low++)
        {
            var id = (int)(_pathKeys[low] & uint.MaxValue);
            if (string.Equals(GetPath(id, pathCache), path, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return -1;
    }

    internal override IEnumerable<int> Roots() => _roots;

    internal override IEnumerable<int> Posting(string token)
    {
        if (!_terms.TryGetValue(token, out var term)) yield break;
        var position = _buffer.ReadInt32(_termRows + term * 16 + 8);
        var count = _buffer.ReadInt32(_termRows + term * 16 + 12);
        var id = 0;
        for (var index = 0; index < count; index++)
        {
            if (_varint)
            {
                uint delta = 0;
                var shift = 0;
                byte part;
                do
                {
                    if (shift > 28) throw new InvalidDataException("Kompakt posting geçersiz.");
                    part = _buffer.ReadByte(position++);
                    delta |= (uint)(part & 127) << shift;
                    shift += 7;
                } while ((part & 128) != 0);
                id = checked(id + (int)delta);
            }
            else
            {
                id = _buffer.ReadInt32(position);
                position += 4;
            }
            if ((uint)id >= (uint)ItemCount) throw new InvalidDataException("Kompakt öğe kimliği geçersiz.");
            yield return id;
        }
    }

    internal override IEnumerable<int> Children(string path, Dictionary<int, string>? pathCache = null)
    {
        var id = Find(path, pathCache);
        if (id < 0)
        {
            if (_orphanChildren.TryGetValue(path, out var children))
                foreach (var child in children) yield return child;
            yield break;
        }
        int start, count;
        if (_rowSize == 80)
        {
            var row = Row(id);
            start = _buffer.ReadInt32(row + 64);
            count = _buffer.ReadInt32(row + 68);
        }
        else
        {
            var low = 0;
            var high = _parentGroupCount;
            while (low < high)
            {
                var middle = low + (high - low) / 2;
                if (_buffer.ReadInt32(_children + 4 + middle * 12) < id) low = middle + 1;
                else high = middle;
            }
            if (low == _parentGroupCount) yield break;
            var group = _children + 4 + low * 12;
            if (_buffer.ReadInt32(group) != id) yield break;
            start = _buffer.ReadInt32(group + 4);
            count = _buffer.ReadInt32(group + 8);
        }
        for (var index = 0; index < count; index++) yield return _buffer.ReadInt32(start + index * 4);
    }

    internal override string[] ItemTokens(int id)
    {
        var row = Row(id);
        var start = _buffer.ReadInt32(row + (_rowSize == 80 ? 72 : 60));
        var count = _rowSize == 80
            ? _buffer.ReadInt32(row + 76)
            : ((id + 1 < ItemCount ? _buffer.ReadInt32(Row(id + 1) + 60) : _buffer.ReadInt32(32)) - start) / 4;
        var result = new string[count];
        for (var index = 0; index < result.Length; index++) result[index] = ReadTerm(_buffer.ReadInt32(start + index * 4));
        return result;
    }

    private string ReadTerm(int id)
    {
        var row = checked(_termRows + id * 16);
        return _buffer.ReadString(_buffer.ReadInt32(row), _buffer.ReadInt32(row + 4));
    }

    private int Row(int id)
    {
        if ((uint)id >= (uint)ItemCount) throw new InvalidDataException("Kompakt öğe kimliği geçersiz.");
        return checked(_rows + id * _rowSize);
    }

    private string ReadExternalParent(int row)
    {
        var offset = _buffer.ReadInt32(row + 24);
        return _rowSize == 80
            ? _buffer.ReadString(offset, _buffer.ReadInt32(row + 28))
            : _buffer.ReadString(checked(offset + 4), _buffer.ReadInt32(offset));
    }

    private string GetPath(int id, Dictionary<int, string>? cache = null)
    {
        if (cache != null)
        {
            var pending = new Stack<int>();
            var prefix = string.Empty;
            while (id >= 0)
            {
                if (cache.TryGetValue(id, out var existing)) { prefix = existing; break; }
                if (pending.Count >= ItemCount) throw new InvalidDataException("Kompakt yol döngüsü.");
                pending.Push(id);
                id = _buffer.ReadInt32(Row(id) + 16);
            }
            while (pending.TryPop(out id))
            {
                var row = Row(id);
                prefix += _buffer.ReadString(_buffer.ReadInt32(row + 8), _buffer.ReadInt32(row + 12));
                cache[id] = prefix;
            }
            return prefix;
        }
        var fragments = new List<string>();
        var count = 0;
        while (id >= 0)
        {
            if (++count > ItemCount) throw new InvalidDataException("Kompakt yol döngüsü.");
            var row = Row(id);
            fragments.Add(_buffer.ReadString(_buffer.ReadInt32(row + 8), _buffer.ReadInt32(row + 12)));
            id = _buffer.ReadInt32(row + 16);
        }
        fragments.Reverse();
        return string.Concat(fragments);
    }

    private static long PathKey(string path, int id) =>
        ((long)StringComparer.OrdinalIgnoreCase.GetHashCode(path) << 32) | (uint)id;

    private long ReadPathKey(int id, ref char[] buffer)
    {
        var length = 0;
        var links = 0;
        for (var prefix = id; prefix >= 0; prefix = _buffer.ReadInt32(Row(prefix) + 16))
        {
            if (++links > ItemCount) throw new InvalidDataException("Kompakt yol döngüsü.");
            var fragmentLength = _buffer.ReadInt32(Row(prefix) + 12);
            if (fragmentLength < 0) throw new InvalidDataException("Kompakt yol uzunluğu geçersiz.");
            length = checked(length + fragmentLength);
        }
        if (length > buffer.Length) buffer = new char[length];
        var offset = length;
        for (var prefix = id; prefix >= 0; prefix = _buffer.ReadInt32(Row(prefix) + 16))
        {
            var row = Row(prefix);
            var count = _buffer.ReadInt32(row + 12);
            offset -= count;
            _buffer.CopyChars(_buffer.ReadInt32(row + 8), buffer, offset, count);
        }
        return ((long)string.GetHashCode(buffer.AsSpan(0, length), StringComparison.OrdinalIgnoreCase) << 32) | (uint)id;
    }

    internal override void WriteNew(string path)
    {
        using var stream = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[65536];
        for (var offset = 0; offset < PayloadBytes;)
        {
            var count = Math.Min(bytes.Length, PayloadBytes - offset);
            _buffer.CopyTo(offset, bytes, count);
            stream.Write(bytes, 0, count);
            hash.AppendData(bytes, 0, count);
            offset += count;
        }
        stream.Write(hash.GetHashAndReset());
        stream.Flush(flushToDisk: true);
    }

    internal static CompactCatalog OpenMapped(string path, bool verify = true)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read | FileShare.Delete);
        try
        {
            if (stream.Length < HeaderSize + 32 || stream.Length > int.MaxValue)
                throw new InvalidDataException("Kompakt katalog boyutu geçersiz.");
            var payloadLength = (int)stream.Length - 32;
            if (verify)
            {
                using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
                var bytes = new byte[65536];
                for (var offset = 0; offset < payloadLength;)
                {
                    var count = Math.Min(bytes.Length, payloadLength - offset);
                    stream.ReadExactly(bytes, 0, count);
                    hash.AppendData(bytes, 0, count);
                    offset += count;
                }
                var expected = new byte[32];
                stream.ReadExactly(expected);
                if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                    throw new InvalidDataException("Kompakt katalog sağlama toplamı geçersiz.");
            }
            var mapped = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read,
                HandleInheritability.None, leaveOpen: false);
            MappedBuffer? buffer = null;
            try
            {
                buffer = new MappedBuffer(mapped, mapped.CreateViewAccessor(0, payloadLength, MemoryMappedFileAccess.Read), payloadLength);
                return new CompactCatalog(buffer);
            }
            catch
            {
                if (buffer != null) buffer.CloseUnpublished();
                else mapped.Dispose();
                throw;
            }
        }
        catch
        {
            stream.Dispose();
            throw;
        }
    }

    private abstract class CatalogBuffer
    {
        internal abstract int Length { get; }
        internal abstract int ReadInt32(int offset);
        internal abstract long ReadInt64(int offset);
        internal abstract byte ReadByte(int offset);
        internal abstract string ReadString(int offset, int length);
        internal abstract void CopyChars(int offset, char[] target, int targetOffset, int count);
        internal abstract void CopyTo(int offset, byte[] target, int count);
    }

    private sealed class ArrayBuffer(byte[] data) : CatalogBuffer
    {
        internal override int Length => data.Length;
        internal override int ReadInt32(int offset) => BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, 4));
        internal override long ReadInt64(int offset) => BinaryPrimitives.ReadInt64LittleEndian(data.AsSpan(offset, 8));
        internal override byte ReadByte(int offset) => data[offset];
        internal override string ReadString(int offset, int length) =>
            new(MemoryMarshal.Cast<byte, char>(data.AsSpan(offset, checked(length * 2))));
        internal override void CopyChars(int offset, char[] target, int targetOffset, int count) =>
            MemoryMarshal.Cast<byte, char>(data.AsSpan(offset, checked(count * 2))).CopyTo(target.AsSpan(targetOffset, count));
        internal override void CopyTo(int offset, byte[] target, int count) => data.AsSpan(offset, count).CopyTo(target);
    }

    private sealed class MappedBuffer(MemoryMappedFile file, MemoryMappedViewAccessor view, int length) : CatalogBuffer
    {
        internal override int Length => length;
        internal override int ReadInt32(int offset) => view.ReadInt32(offset);
        internal override long ReadInt64(int offset) => view.ReadInt64(offset);
        internal override byte ReadByte(int offset) => view.ReadByte(offset);
        internal override void CopyChars(int offset, char[] target, int targetOffset, int count)
        {
            if (offset < 0 || count < 0 || (long)offset + (long)count * 2 > length)
                throw new InvalidDataException("Kompakt metin sınırı geçersiz.");
            view.ReadArray(offset, target, targetOffset, count);
        }
        internal override string ReadString(int offset, int count)
        {
            if (offset < 0 || count < 0 || (long)offset + (long)count * 2 > length)
                throw new InvalidDataException("Kompakt metin sınırı geçersiz.");
            var characters = new ushort[count];
            view.ReadArray(offset, characters, 0, count);
            return new string(MemoryMarshal.Cast<ushort, char>(characters));
        }
        internal override void CopyTo(int offset, byte[] target, int count) => view.ReadArray(offset, target, 0, count);
        internal void CloseUnpublished()
        {
            view.Dispose();
            file.Dispose();
            GC.SuppressFinalize(this);
        }
        ~MappedBuffer()
        {
            view.Dispose();
            file.Dispose();
        }
    }
}
