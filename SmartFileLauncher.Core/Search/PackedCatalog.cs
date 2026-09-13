using System.Buffers;
using System.Buffers.Binary;
using System.Security.Cryptography;
using System.Text.Json;

namespace SmartFileLauncher.Core.Search;

internal sealed class PackedCatalog : CatalogReader, IDirectCatalogMatches
{
    internal override bool RequiresOwnCheckpoint => true;
    IEnumerable<int> IDirectCatalogMatches.MatchingItems(string token, int? distance, CancellationToken ct) => MatchingItems(token, distance, ct);
    string IDirectCatalogMatches.CanonicalToken(string token) => CanonicalToken(token);
    private readonly PackedMapping _map;
    private readonly int _termRows, _lookup, _children, _itemTokens, _postings, _metadata, _text, _state, _groups, _maxTerm;
    private readonly long _indexedUtc;
    private readonly long[] _pathKeys;
    private readonly int[] _roots;
    private readonly Dictionary<string, List<int>> _orphans = new(StringComparer.OrdinalIgnoreCase);
    internal override int ItemCount { get; }
    internal override int TokenCount { get; }
    internal override int MissingParentCount { get; }
    internal override int PayloadBytes { get; }
    internal override bool UsesVarint => true;
    internal override long Generation { get; }
    internal PackedCheckpoint Checkpoint { get; }
    internal long BaseFileLength => (long)PayloadBytes + 32;
    internal void RetireFile(string path) => _map.RetireFile(path);
    internal override IEnumerable<string> Tokens => Enumerable.Range(0, TokenCount).Select(ReadTerm);

    private PackedCatalog(PackedMapping map)
    {
        _map = map;
        ItemCount = map.Int32(8); TokenCount = map.Int32(12);
        _termRows = map.Int32(20); _children = map.Int32(24); _itemTokens = map.Int32(28);
        _postings = map.Int32(32); _text = map.Int32(36); PayloadBytes = map.Int32(40);
        Generation = map.Int64(48); _metadata = map.Int32(56); _lookup = map.Int32(60);
        _state = map.Int32(64); _maxTerm = map.Int32(68); _indexedUtc = map.Int64(80);
        if (ItemCount < 0 || TokenCount < 0 || _maxTerm < 0 || _maxTerm > 1024 * 1024 ||
            map.Int32(0) != PackedFormat.Magic || map.Int32(4) != 4 || map.Int32(16) != PackedFormat.HeaderSize ||
            map.Int32(72) != PackedFormat.RowSize || map.Int32(44) != 1 || PayloadBytes != map.Length ||
            (long)PackedFormat.HeaderSize + (long)ItemCount * PackedFormat.RowSize != _termRows ||
            (long)_termRows + (long)TokenCount * 12 != _lookup || (long)_lookup + (long)TokenCount * 4 != _children ||
            _children < _lookup || _children + 4L > _itemTokens || _itemTokens > _postings ||
            _postings > _metadata || _metadata > _text || _text > _state || _state > PayloadBytes)
            throw new InvalidDataException("Packed katalog bölüm sınırları geçersiz.");
        _groups = map.Int32(_children);
        if (_groups < 0 || _groups > ItemCount || (long)_children + 4 + (long)_groups * 12 > _itemTokens)
            throw new InvalidDataException("Packed children tablosu geçersiz.");
        var stateBytes = new byte[PayloadBytes - _state];
        map.Copy(_state, stateBytes);
        Checkpoint = JsonSerializer.Deserialize<PackedCheckpoint>(stateBytes) ?? throw new InvalidDataException("Packed checkpoint eksik.");
        if (Checkpoint.Contract != PackedCheckpoint.CurrentContract || Checkpoint.Sequence < 0)
            throw new InvalidDataException("Packed sözleşme veya checkpoint uyuşmuyor.");
        _pathKeys = new long[ItemCount];
        var roots = new List<int>();
        var pathBuffer = new char[256];
        for (var id = 0; id < ItemCount; id++)
        {
            var row = Row(id);
            var parent = map.Int32(row);
            var name = map.Int32(row + 4); var meta = map.Int32(row + 8); var tokens = map.Int32(row + 12);
            if (parent < -1 || parent >= id || name < _text || name >= _state || meta < _metadata || meta >= _text ||
                tokens < _itemTokens || tokens > _postings) throw new InvalidDataException("Packed kayıt sınırları geçersiz.");
            var length = PathLength(id);
            if (length > pathBuffer.Length) pathBuffer = new char[length];
            CopyPath(id, pathBuffer.AsSpan(0, length));
            _pathKeys[id] = ((long)string.GetHashCode(pathBuffer.AsSpan(0, length), StringComparison.OrdinalIgnoreCase) << 32) | (uint)id;
            if (parent < 0)
            {
                var entry = GetItem(id);
                if (string.IsNullOrEmpty(entry.ParentPath)) roots.Add(id); else MissingParentCount++;
                if (entry.ParentPath is { } external)
                {
                    if (!_orphans.TryGetValue(external, out var children)) _orphans[external] = children = [];
                    children.Add(id);
                }
            }
        }
        _roots = roots.ToArray();
        Array.Sort(_pathKeys);
        ValidateLookup();
    }

    internal static PackedCatalog Open(string path)
    {
        var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        PackedMapping? map = null;
        try
        {
            Span<byte> header = stackalloc byte[PackedFormat.HeaderSize];
            stream.ReadExactly(header);
            var length = BinaryPrimitives.ReadInt32LittleEndian(header[40..]);
            if (length < PackedFormat.HeaderSize || (long)length + 32 > stream.Length)
                throw new InvalidDataException("Packed katalog eksik.");
            stream.Position = 0;
            using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
            var buffer = new byte[65536];
            for (var left = length; left > 0;)
            {
                var count = Math.Min(left, buffer.Length);
                stream.ReadExactly(buffer, 0, count); hash.AppendData(buffer, 0, count); left -= count;
            }
            var expected = new byte[32]; stream.ReadExactly(expected);
            if (!CryptographicOperations.FixedTimeEquals(hash.GetHashAndReset(), expected))
                throw new InvalidDataException("Packed katalog checksum uyuşmuyor.");
            map = new PackedMapping(stream, length);
            return new PackedCatalog(map);
        }
        catch { map?.CloseUnpublished(); stream.Dispose(); throw; }
    }

    private int Row(int id)
    {
        if ((uint)id >= (uint)ItemCount) throw new InvalidDataException("Packed öğe kimliği geçersiz.");
        return checked(PackedFormat.HeaderSize + id * PackedFormat.RowSize);
    }
    private uint Flags(int id)
    {
        var offset = _map.Int32(Row(id) + 8);
        return checked((uint)_map.Unsigned(ref offset, _text));
    }
    private (int Offset, int Bytes, bool Utf16, bool Null) Segment(int id, out int prefix)
    {
        var row = Row(id);
        var offset = _map.Int32(row + 4);
        var range = _map.TextRange(ref offset, _state);
        var complex = (Flags(id) & PackedFormat.ComplexPath) != 0;
        prefix = complex ? -1 : _map.Int32(row);
        if (complex) range = _map.TextRange(ref offset, _state);
        if (range.Null) throw new InvalidDataException("Packed yol eksik.");
        return range;
    }
    private int PathLength(int id)
    {
        var length = 0;
        while (id >= 0)
        {
            var range = Segment(id, out var prefix);
            length = checked(length + _map.TextCharCount(range) + (prefix >= 0 ? 1 : 0));
            id = prefix;
        }
        return length;
    }
    private void CopyPath(int id, Span<char> target)
    {
        var position = target.Length;
        while (id >= 0)
        {
            var range = Segment(id, out var prefix);
            var count = _map.TextCharCount(range);
            position -= count;
            _map.Decode(range, target.Slice(position, count));
            if (prefix >= 0) target[--position] = '\\';
            id = prefix;
        }
        if (position != 0) throw new InvalidDataException("Packed yol uzunluğu uyuşmuyor.");
    }
    private string PathAt(int id, Dictionary<int, string>? cache = null)
    {
        if (cache is not null && cache.TryGetValue(id, out var existing)) return existing;
        var value = string.Create(PathLength(id), (Catalog: this, Id: id), static (span, state) => state.Catalog.CopyPath(state.Id, span));
        if (cache is not null) cache[id] = value;
        return value;
    }

    private readonly record struct Metadata(uint Flags, long ModifiedUtc, long CreatedUtc, long? Size,
        int OpenCount, DateTime? Created, DateTime? Modified, long Indexed);

    private Metadata ReadMetadata(int id)
    {
        var row = Row(id); var offset = _map.Int32(row + 8);
        var flags = checked((uint)_map.Unsigned(ref offset, _text));
        if ((flags & ~65535u) != 0) throw new InvalidDataException("Packed bayrak geçersiz.");
        var modifiedUtc = _map.Int64(offset); offset += 8;
        var createdUtc = (flags & PackedFormat.RawCreated) != 0 ? checked(modifiedUtc + PackedFormat.DecodeSigned(_map.Unsigned(ref offset, _text))) : 0;
        long? size = (flags & 2) != 0 ? PackedFormat.DecodeSigned(_map.Unsigned(ref offset, _text)) : null;
        var openCount = (flags & PackedFormat.OpenCount) != 0 ? checked((int)PackedFormat.DecodeSigned(_map.Unsigned(ref offset, _text))) : 0;
        DateTime? created = (flags & 4) != 0 ? PackedFormat.RestoreDate(createdUtc, (DateTimeKind)((flags >> 4) & 3)) : null;
        DateTime? modified = (flags & 8) != 0 ? PackedFormat.RestoreDate(modifiedUtc, (DateTimeKind)((flags >> 6) & 3)) : null;
        if ((flags & PackedFormat.CreatedOverride) != 0) { created = new DateTime(_map.Int64(offset), (DateTimeKind)((flags >> 4) & 3)); offset += 8; }
        if ((flags & PackedFormat.ModifiedOverride) != 0) { modified = new DateTime(_map.Int64(offset), (DateTimeKind)((flags >> 6) & 3)); offset += 8; }
        var indexed = (flags & PackedFormat.IndexedOverride) != 0 ? checked(_indexedUtc + PackedFormat.DecodeSigned(_map.Unsigned(ref offset, _text))) : _indexedUtc;
        var expectedEnd = id + 1 < ItemCount ? _map.Int32(Row(id + 1) + 8) : _text;
        if (offset != expectedEnd) throw new InvalidDataException("Packed metadata uzunluğu uyuşmuyor.");
        return new(flags, modifiedUtc, createdUtc, size, openCount, created, modified, indexed);
    }

    internal PackedRecord GetRecord(int id, Dictionary<int, string>? cache = null, bool transient = false)
    {
        var row = Row(id);
        var meta = ReadMetadata(id);
        var flags = meta.Flags;
        var text = _map.Int32(row + 4);
        var name = _map.Text(ref text, _state) ?? throw new InvalidDataException("Packed ad eksik.");
        var parent = _map.Int32(row);
        string fullPath; string? parentPath;
        if ((flags & PackedFormat.ComplexPath) != 0)
        {
            fullPath = _map.Text(ref text, _state) ?? throw new InvalidDataException("Packed tam yol eksik.");
            parentPath = _map.Text(ref text, _state);
        }
        else
        {
            parentPath = parent < 0 ? null : PathAt(parent, cache);
            fullPath = transient ? parentPath + "\\" + name : PathAt(id, cache);
        }
        return new(new(name, fullPath, (flags & 1) != 0, meta.Size, meta.Created, meta.Modified, meta.OpenCount, parentPath),
            meta.ModifiedUtc, meta.CreatedUtc, (flags & PackedFormat.Hidden) != 0, (flags & PackedFormat.System) != 0, meta.Indexed);
    }
    internal override SearchItem GetItem(int id, Dictionary<int, string>? pathCache = null) => GetRecord(id, pathCache).Item;
    internal override SearchItem GetTransientItem(int id, Dictionary<int, string> sharedPrefixPaths) => GetRecord(id, sharedPrefixPaths, true).Item;
    internal override bool Matches(int id, QueryCatalogFilter filter)
    {
        var meta = ReadMetadata(id);
        return filter.Matches((meta.Flags & 1) != 0, meta.Size, meta.Created, meta.Modified);
    }
    internal override int Find(string path, Dictionary<int, string>? pathCache = null)
    {
        var key = (long)StringComparer.OrdinalIgnoreCase.GetHashCode(path) << 32;
        var low = 0; var high = _pathKeys.Length;
        while (low < high) { var middle = low + (high - low) / 2; if (_pathKeys[middle] < key) low = middle + 1; else high = middle; }
        for (; low < _pathKeys.Length && (_pathKeys[low] >> 32) == (key >> 32); low++)
        {
            var id = (int)_pathKeys[low];
            if (string.Equals(path, PathAt(id, pathCache), StringComparison.OrdinalIgnoreCase)) return id;
        }
        return -1;
    }
    internal override IEnumerable<int> Roots() => _roots;

    private string ReadTerm(int term)
    {
        if ((uint)term >= (uint)TokenCount) throw new InvalidDataException("Packed token kimliği geçersiz.");
        var offset = _map.Int32(_termRows + term * 12);
        return _map.Text(ref offset, _state) ?? throw new InvalidDataException("Packed token eksik.");
    }
    private int DecodeTerm(int term, char[] buffer)
    {
        if ((uint)term >= (uint)TokenCount) throw new InvalidDataException("Packed token kimliği geçersiz.");
        var offset = _map.Int32(_termRows + term * 12);
        var range = _map.TextRange(ref offset, _state);
        return _map.Decode(range, buffer);
    }
    private int FindTerm(string token)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Math.Max(_maxTerm, 1));
        try
        {
            var low = 0; var high = TokenCount;
            while (low < high)
            {
                var middle = low + (high - low) / 2; var term = _map.Int32(_lookup + middle * 4);
                var length = DecodeTerm(term, buffer);
                var compare = MemoryExtensions.CompareTo(buffer.AsSpan(0, length), token.AsSpan(), StringComparison.OrdinalIgnoreCase);
                if (compare < 0) low = middle + 1; else if (compare > 0) high = middle; else return term;
            }
            return -1;
        }
        finally { ArrayPool<char>.Shared.Return(buffer); }
    }
    private void ValidateLookup()
    {
        var previous = ArrayPool<char>.Shared.Rent(Math.Max(_maxTerm, 1));
        var current = ArrayPool<char>.Shared.Rent(Math.Max(_maxTerm, 1));
        try
        {
            var previousLength = 0;
            for (var slot = 0; slot < TokenCount; slot++)
            {
                var length = DecodeTerm(_map.Int32(_lookup + slot * 4), current);
                if (slot > 0 && MemoryExtensions.CompareTo(previous.AsSpan(0, previousLength), current.AsSpan(0, length), StringComparison.OrdinalIgnoreCase) >= 0)
                    throw new InvalidDataException("Packed token sırası geçersiz.");
                (previous, current) = (current, previous); previousLength = length;
            }
        }
        finally { ArrayPool<char>.Shared.Return(previous); ArrayPool<char>.Shared.Return(current); }
    }
    internal override bool ContainsToken(string token) => FindTerm(token) >= 0;
    internal string CanonicalToken(string token) => FindTerm(token) is var term && term >= 0 ? ReadTerm(term) : token;
    internal override IEnumerable<int> Posting(string token)
    {
        var term = FindTerm(token);
        if (term < 0) yield break;
        foreach (var id in PostingByTerm(term)) yield return id;
    }
    private IEnumerable<int> PostingByTerm(int term)
    {
        var offset = _map.Int32(_termRows + term * 12 + 4);
        var count = _map.Int32(_termRows + term * 12 + 8);
        var id = 0;
        for (var i = 0; i < count; i++)
        {
            id = checked(id + (int)_map.Unsigned(ref offset, _metadata));
            if ((uint)id >= (uint)ItemCount) throw new InvalidDataException("Packed posting kimliği geçersiz.");
            yield return id;
        }
    }
    internal IEnumerable<int> MatchingItems(string token, int? distance, CancellationToken ct)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Math.Max(_maxTerm, 1));
        var scratch = distance.HasValue ? ArrayPool<int>.Shared.Rent(checked(2 * (_maxTerm + 1))) : null;
        try
        {
            for (var term = 0; term < TokenCount; term++)
            {
                ct.ThrowIfCancellationRequested();
                var length = DecodeTerm(term, buffer);
                if (!TermMatches(token, buffer, length, distance, scratch)) continue;
                foreach (var id in PostingByTerm(term)) { ct.ThrowIfCancellationRequested(); yield return id; }
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer); if (scratch is not null) ArrayPool<int>.Shared.Return(scratch); }
    }
    internal override IEnumerable<string> MatchingTokens(string token, int? distance, CancellationToken ct)
    {
        var buffer = ArrayPool<char>.Shared.Rent(Math.Max(_maxTerm, 1));
        var scratch = distance.HasValue ? ArrayPool<int>.Shared.Rent(checked(2 * (_maxTerm + 1))) : null;
        try
        {
            for (var term = 0; term < TokenCount; term++)
            {
                ct.ThrowIfCancellationRequested();
                var length = DecodeTerm(term, buffer);
                if (TermMatches(token, buffer, length, distance, scratch)) yield return new string(buffer, 0, length);
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer); if (scratch is not null) ArrayPool<int>.Shared.Return(scratch); }
    }
    private static bool TermMatches(string source, char[] target, int length, int? distance, int[]? scratch)
    {
        if (distance is not int max) return MemoryExtensions.Contains(target.AsSpan(0, length), source.AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (source.Length == 0 || length == 0 || max < 0 || Math.Abs(source.Length - length) > max) return false;
        var width = length + 1;
        var previous = scratch.AsSpan(0, width); var current = scratch.AsSpan(width, width);
        for (var j = 0; j <= length; j++) previous[j] = j;
        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i; var minimum = i;
            for (var j = 1; j <= length; j++)
            { current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1)); minimum = Math.Min(minimum, current[j]); }
            if (minimum > max) return false;
            var swap = previous; previous = current; current = swap;
        }
        return previous[length] <= max;
    }
    internal override IEnumerable<int> Children(string path, Dictionary<int, string>? pathCache = null)
    {
        var id = Find(path, pathCache);
        if (id < 0) { if (_orphans.TryGetValue(path, out var children)) foreach (var child in children) yield return child; yield break; }
        var low = 0; var high = _groups;
        while (low < high) { var middle = low + (high - low) / 2; if (_map.Int32(_children + 4 + middle * 12) < id) low = middle + 1; else high = middle; }
        if (low == _groups) yield break;
        var group = _children + 4 + low * 12;
        if (_map.Int32(group) != id) yield break;
        var start = _map.Int32(group + 4); var count = _map.Int32(group + 8);
        for (var i = 0; i < count; i++) yield return _map.Int32(start + i * 4);
    }
    internal override string[] ItemTokens(int id)
    {
        var offset = _map.Int32(Row(id) + 12);
        var end = id + 1 < ItemCount ? _map.Int32(Row(id + 1) + 12) : _postings;
        var result = new List<string>();
        while (offset < end) result.Add(ReadTerm(checked((int)_map.Unsigned(ref offset, end))));
        return result.ToArray();
    }
    internal override void WriteNew(string path)
    {
        using var output = new FileStream(path, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        var bytes = new byte[65536];
        for (var offset = 0; offset < PayloadBytes;)
        { var count = Math.Min(bytes.Length, PayloadBytes - offset); _map.Copy(offset, bytes.AsSpan(0, count)); output.Write(bytes, 0, count); hash.AppendData(bytes, 0, count); offset += count; }
        output.Write(hash.GetHashAndReset()); output.Flush(true);
    }
}
