using System.Buffers;
using System.Text;
using System.Text.Unicode;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

internal sealed class IndexNames
{
    private const byte WideFlag = 1;
    private static readonly SearchValues<byte> WordBreaks = SearchValues.Create(" _-.,[]()"u8);
    private byte[] _original = new byte[1 << 16];
    private byte[] _folded = new byte[1 << 16];
    private int _originalUsed;
    private int _foldedUsed;
    private int[] _originalOffset = new int[256];
    private int[] _foldedOffset = new int[256];
    private ushort[] _originalLength = new ushort[256];
    private ushort[] _foldedLength = new ushort[256];
    private byte[] _flags = new byte[256];
    private int _count;
    private int[] _buckets = new int[1024];
    private int[] _next = new int[256];
    private byte[] _plain = new byte[1 << 12];
    private int _plainUsed;
    private int[] _plainOffset = new int[64];
    private int[] _plainName = new int[64];
    private int _plainCount;

    internal int Count => _count;

    internal ReadOnlySpan<byte> Original(int id) => _original.AsSpan(_originalOffset[id], _originalLength[id]);

    internal ReadOnlySpan<byte> Folded(int id) => _folded.AsSpan(_foldedOffset[id], _foldedLength[id]);

    internal bool IsWide(int id) => (_flags[id] & WideFlag) != 0;

    internal bool IsAscii(int id) => !IsWide(id) && Ascii.IsValid(Original(id));

    internal string Text(int id)
    {
        var bytes = Original(id);
        return IsWide(id) ? new string(System.Runtime.InteropServices.MemoryMarshal.Cast<byte, char>(bytes)) : Encoding.UTF8.GetString(bytes);
    }

    internal int Compare(int left, int right)
    {
        if (left == right) return 0;
        var byFolded = Folded(left).SequenceCompareTo(Folded(right));
        if (byFolded != 0) return byFolded;
        var byOriginal = Original(left).SequenceCompareTo(Original(right));
        return byOriginal != 0 ? byOriginal : _flags[left].CompareTo(_flags[right]);
    }

    internal int Intern(ReadOnlySpan<char> name)
    {
        if (name.IsEmpty) throw new ArgumentException("Dosya adı boş olamaz.", nameof(name));
        var rented = ArrayPool<byte>.Shared.Rent(name.Length * 3 + 8);
        try
        {
            var flags = (byte)0;
            ReadOnlySpan<byte> original;
            if (Utf8.FromUtf16(name, rented, out _, out var written, replaceInvalidSequences: false) == OperationStatus.Done)
                original = rented.AsSpan(0, written);
            else
            {
                flags = WideFlag;
                original = System.Runtime.InteropServices.MemoryMarshal.AsBytes(name);
            }
            var hash = Hash(original, flags);
            for (var id = _buckets[hash & (_buckets.Length - 1)] - 1; id >= 0; id = _next[id] - 1)
                if (_flags[id] == flags && Original(id).SequenceEqual(original)) return id;
            return Append(name, original, flags, hash);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private int Append(ReadOnlySpan<char> name, ReadOnlySpan<byte> original, byte flags, uint hash)
    {
        if (original.Length > ushort.MaxValue) throw new ArgumentException("Dosya adı çok uzun.", nameof(name));
        var id = _count;
        if (id == _originalOffset.Length) GrowEntries();
        EnsureCapacity(ref _original, _originalUsed + original.Length);
        original.CopyTo(_original.AsSpan(_originalUsed));
        _originalOffset[id] = _originalUsed;
        _originalLength[id] = checked((ushort)original.Length);
        _originalUsed += original.Length;

        var upper = ArrayPool<char>.Shared.Rent(name.Length);
        try
        {
            var upperName = upper.AsSpan(0, name.Length);
            name.ToUpperInvariant(upperName);
            EnsureCapacity(ref _folded, _foldedUsed + upperName.Length * 3 + 1);
            Utf8.FromUtf16(upperName, _folded.AsSpan(_foldedUsed), out _, out var written, replaceInvalidSequences: true);
            _foldedOffset[id] = _foldedUsed;
            _foldedLength[id] = checked((ushort)written);
            _foldedUsed += written;
            _folded[_foldedUsed++] = 0;
        }
        finally { ArrayPool<char>.Shared.Return(upper); }

        _flags[id] = flags;
        if (flags == 0 && !Ascii.IsValid(original)) AppendPlain(id, name.ToString());
        _count++;
        if (_count > _buckets.Length) Rehash(_buckets.Length * 2);
        else Link(id, hash);
        return id;
    }

    private void AppendPlain(int id, string text)
    {
        var plain = Encoding.UTF8.GetBytes(Plain(text));
        if (plain.Length == 0 || Array.IndexOf(plain, (byte)0) >= 0 || Folded(id).SequenceEqual(plain)) return;
        if (_plainCount == _plainOffset.Length)
        {
            var size = _plainOffset.Length + Math.Max(64, _plainOffset.Length / 2);
            Array.Resize(ref _plainOffset, size);
            Array.Resize(ref _plainName, size);
        }
        EnsureCapacity(ref _plain, _plainUsed + plain.Length + 1);
        plain.CopyTo(_plain, _plainUsed);
        _plainOffset[_plainCount] = _plainUsed;
        _plainName[_plainCount++] = id;
        _plainUsed += plain.Length;
        _plain[_plainUsed++] = 0;
    }

    private void Link(int id, uint hash)
    {
        var bucket = (int)(hash & (uint)(_buckets.Length - 1));
        _next[id] = _buckets[bucket];
        _buckets[bucket] = id + 1;
    }

    private void Rehash(int size)
    {
        _buckets = new int[size];
        for (var id = 0; id < _count; id++) Link(id, Hash(Original(id), _flags[id]));
    }

    private void GrowEntries()
    {
        var size = Math.Max(256, _originalOffset.Length + _originalOffset.Length / 2);
        Array.Resize(ref _originalOffset, size);
        Array.Resize(ref _foldedOffset, size);
        Array.Resize(ref _originalLength, size);
        Array.Resize(ref _foldedLength, size);
        Array.Resize(ref _flags, size);
        Array.Resize(ref _next, size);
    }

    internal void Trim()
    {
        Array.Resize(ref _original, _originalUsed + Math.Max(1 << 16, _originalUsed / 64));
        Array.Resize(ref _folded, _foldedUsed + Math.Max(1 << 16, _foldedUsed / 64));
        var size = _count + Math.Max(256, _count / 64);
        Array.Resize(ref _originalOffset, size);
        Array.Resize(ref _foldedOffset, size);
        Array.Resize(ref _originalLength, size);
        Array.Resize(ref _foldedLength, size);
        Array.Resize(ref _flags, size);
        Array.Resize(ref _next, size);
        Array.Resize(ref _plain, _plainUsed + Math.Max(1 << 12, _plainUsed / 64));
        var plainSize = _plainCount + Math.Max(64, _plainCount / 64);
        Array.Resize(ref _plainOffset, plainSize);
        Array.Resize(ref _plainName, plainSize);
    }

    private static void EnsureCapacity(ref byte[] buffer, int required)
    {
        if (required <= buffer.Length) return;
        var size = buffer.Length;
        while (size < required) size = checked(size + Math.Max(size / 2, 1 << 16));
        Array.Resize(ref buffer, size);
    }

    private static uint Hash(ReadOnlySpan<byte> bytes, byte flags)
    {
        var hash = 14695981039346656037UL ^ flags;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return (uint)(hash ^ hash >> 32);
    }

    internal static byte[] FoldQuery(string query)
    {
        var upper = query.ToUpperInvariant();
        return Encoding.UTF8.GetBytes(upper);
    }

    internal static string Plain(string text) => SearchTextNormalizer.Fold(text).ToUpperInvariant();

    internal static bool IsWordBreak(byte value) => WordBreaks.Contains(value);

    internal ulong[] Match(byte[] pattern, CancellationToken cancellationToken)
    {
        var bits = new ulong[(_count + 63) >> 6];
        Scan(_folded, _foldedUsed, _foldedOffset, null, _count, pattern, false, bits, cancellationToken);
        return bits;
    }

    internal ulong[] Find(byte[] upper, byte[] plain, bool wholeWord, CancellationToken cancellationToken)
    {
        var bits = new ulong[(_count + 63) >> 6];
        Scan(_folded, _foldedUsed, _foldedOffset, null, _count, upper, wholeWord, bits, cancellationToken);
        if (!plain.AsSpan().SequenceEqual(upper))
            Scan(_folded, _foldedUsed, _foldedOffset, null, _count, plain, wholeWord, bits, cancellationToken);
        Scan(_plain, _plainUsed, _plainOffset, _plainName, _plainCount, plain, wholeWord, bits, cancellationToken);
        return bits;
    }

    private static void Scan(byte[] pool, int used, int[] offsets, int[]? owners, int count, byte[] pattern, bool wholeWord,
        ulong[] bits, CancellationToken cancellationToken)
    {
        if (pattern.Length == 0 || count == 0 || Array.IndexOf(pattern, (byte)0) >= 0) return;
        var chunks = used < (1 << 20) ? 1 : Math.Clamp(Environment.ProcessorCount, 1, 16);
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancellationToken }, chunk =>
        {
            var start = (int)((long)used * chunk / chunks);
            var end = (int)((long)used * (chunk + 1) / chunks);
            if (chunk > 0)
            {
                var separator = pool.AsSpan(start, end - start).IndexOf((byte)0);
                if (separator < 0) return;
                start += separator + 1;
            }
            var limit = end;
            if (chunk < chunks - 1)
            {
                var separator = pool.AsSpan(end, used - end).IndexOf((byte)0);
                limit = separator < 0 ? used : end + separator;
            }
            var at = start;
            var steps = 0;
            while (at < limit)
            {
                if ((++steps & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var found = pool.AsSpan(at, limit - at).IndexOf(pattern);
                if (found < 0) break;
                var position = at + found;
                var entry = EntryAt(offsets, count, position);
                var entryEnd = entry + 1 < count ? offsets[entry + 1] - 1 : used - 1;
                if (wholeWord && !IsWholeWord(pool, offsets[entry], entryEnd, position, pattern.Length))
                {
                    at = position + 1;
                    continue;
                }
                var id = owners is null ? entry : owners[entry];
                Interlocked.Or(ref bits[id >> 6], 1UL << (id & 63));
                at = entryEnd + 1;
            }
        });
    }

    private static bool IsWholeWord(byte[] pool, int entryStart, int entryEnd, int position, int length) =>
        (position == entryStart || WordBreaks.Contains(pool[position - 1])) &&
        (position + length == entryEnd || WordBreaks.Contains(pool[position + length]));

    private static int EntryAt(int[] offsets, int count, int position)
    {
        var lo = 0;
        var hi = count - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (offsets[mid] <= position) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    internal ReadOnlySpan<byte> FoldedExtension(int id)
    {
        var folded = Folded(id);
        var dot = folded.LastIndexOf((byte)'.');
        return dot < 0 || dot == folded.Length - 1 ? [] : folded[dot..];
    }

    internal void Write(BinaryWriter writer)
    {
        writer.Write(_count);
        writer.Write(_originalUsed);
        writer.Write(_foldedUsed);
        writer.Write(_original, 0, _originalUsed);
        writer.Write(_folded, 0, _foldedUsed);
        IndexArrays.Write(writer, _originalOffset.AsSpan(0, _count));
        IndexArrays.Write(writer, _foldedOffset.AsSpan(0, _count));
        IndexArrays.Write(writer, _originalLength.AsSpan(0, _count));
        IndexArrays.Write(writer, _foldedLength.AsSpan(0, _count));
        writer.Write(_flags, 0, _count);
    }

    internal static IndexNames Read(BinaryReader reader)
    {
        var names = new IndexNames();
        var count = reader.ReadInt32();
        var originalUsed = reader.ReadInt32();
        var foldedUsed = reader.ReadInt32();
        if (count < 0 || originalUsed < 0 || foldedUsed < 0) throw new InvalidDataException("Ad havuzu başlığı geçersiz.");
        names._original = IndexArrays.ReadBytes(reader, originalUsed, originalUsed + Math.Max(1 << 16, originalUsed / 64));
        names._folded = IndexArrays.ReadBytes(reader, foldedUsed, foldedUsed + Math.Max(1 << 16, foldedUsed / 64));
        names._originalUsed = originalUsed;
        names._foldedUsed = foldedUsed;
        var capacity = count + Math.Max(256, count / 64);
        names._originalOffset = IndexArrays.Read<int>(reader, count, capacity);
        names._foldedOffset = IndexArrays.Read<int>(reader, count, capacity);
        names._originalLength = IndexArrays.Read<ushort>(reader, count, capacity);
        names._foldedLength = IndexArrays.Read<ushort>(reader, count, capacity);
        names._flags = IndexArrays.ReadBytes(reader, count, capacity);
        names._next = new int[capacity];
        names._count = count;
        var expected = 0L;
        for (var id = 0; id < count; id++)
        {
            if ((long)names._originalOffset[id] + names._originalLength[id] > originalUsed ||
                names._foldedOffset[id] != expected ||
                (long)names._foldedOffset[id] + names._foldedLength[id] >= foldedUsed ||
                names._folded[names._foldedOffset[id] + names._foldedLength[id]] != 0)
                throw new InvalidDataException("Ad havuzu kaydı geçersiz.");
            expected = names._foldedOffset[id] + names._foldedLength[id] + 1L;
        }
        if (expected != foldedUsed) throw new InvalidDataException("Ad havuzu uzunluğu geçersiz.");
        var buckets = 1024;
        while (buckets < count) buckets *= 2;
        names.Rehash(buckets);
        for (var id = 0; id < count; id++)
            if (!names.IsWide(id) && !Ascii.IsValid(names.Original(id))) names.AppendPlain(id, names.Text(id));
        return names;
    }
}
