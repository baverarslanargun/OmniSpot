using System.Buffers;
using System.Numerics;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Runtime.Intrinsics;
using System.Text;
using System.Text.Unicode;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

internal sealed class IndexNames
{
    private const byte WideFlag = 1;
    private const byte ExactFlag = 2;
    private static readonly SearchValues<byte> WordBreaks = SearchValues.Create(" _-.,[]()"u8);
    private byte[] _pool = new byte[1 << 16];
    private int _poolUsed;
    private int[] _offset = new int[256];
    private ushort[] _length = new ushort[256];
    private byte[] _flags = new byte[256];
    private int _count;
    private int[] _buckets = new int[1024];
    private int[] _next = new int[256];
    private byte[] _exact = new byte[1 << 12];
    private int _exactUsed;
    private int[] _exactName = new int[64];
    private int[] _exactOffset = new int[64];
    private ushort[] _exactLength = new ushort[64];
    private int _exactCount;
    private byte[] _plain = new byte[1 << 12];
    private int _plainUsed;
    private int[] _plainOffset = new int[64];
    private int[] _plainName = new int[64];
    private int _plainCount;

    internal int Count => _count;

    internal ReadOnlySpan<byte> Scanned(int id) => _pool.AsSpan(_offset[id], _length[id]);

    internal ReadOnlySpan<byte> Original(int id)
    {
        if ((_flags[id] & ExactFlag) == 0) return Scanned(id);
        var at = Array.BinarySearch(_exactName, 0, _exactCount, id);
        return _exact.AsSpan(_exactOffset[at], _exactLength[at]);
    }

    internal bool IsWide(int id) => (_flags[id] & WideFlag) != 0;

    internal bool IsAscii(int id) => (_flags[id] & ExactFlag) == 0;

    internal string Text(int id)
    {
        var bytes = Original(id);
        return IsWide(id) ? new string(MemoryMarshal.Cast<byte, char>(bytes)) : Encoding.UTF8.GetString(bytes);
    }

    internal int Compare(int left, int right)
    {
        if (left == right) return 0;
        var byFolded = CompareIgnoreCase(Scanned(left), Scanned(right));
        if (byFolded != 0) return byFolded;
        var byOriginal = Original(left).SequenceCompareTo(Original(right));
        return byOriginal != 0 ? byOriginal : (_flags[left] & WideFlag).CompareTo(_flags[right] & WideFlag);
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
                original = MemoryMarshal.AsBytes(name);
            }
            var hash = Hash(original, flags);
            for (var id = _buckets[hash & (_buckets.Length - 1)] - 1; id >= 0; id = _next[id] - 1)
                if ((_flags[id] & WideFlag) == flags && Original(id).SequenceEqual(original)) return id;
            return Append(name, original, flags, hash);
        }
        finally { ArrayPool<byte>.Shared.Return(rented); }
    }

    private int Append(ReadOnlySpan<char> name, ReadOnlySpan<byte> original, byte flags, uint hash)
    {
        if (original.Length > ushort.MaxValue) throw new ArgumentException("Dosya adı çok uzun.", nameof(name));
        var id = _count;
        if (id == _offset.Length) GrowEntries();
        var exact = flags != 0 || !Ascii.IsValid(original);
        _offset[id] = _poolUsed;
        if (!exact)
        {
            EnsureCapacity(ref _pool, _poolUsed + original.Length + 1);
            original.CopyTo(_pool.AsSpan(_poolUsed));
            _length[id] = checked((ushort)original.Length);
            _poolUsed += original.Length;
        }
        else
        {
            var upper = ArrayPool<char>.Shared.Rent(name.Length);
            try
            {
                var upperName = upper.AsSpan(0, name.Length);
                name.ToUpperInvariant(upperName);
                EnsureCapacity(ref _pool, _poolUsed + upperName.Length * 3 + 1);
                Utf8.FromUtf16(upperName, _pool.AsSpan(_poolUsed), out _, out var written, replaceInvalidSequences: true);
                _length[id] = checked((ushort)written);
                _poolUsed += written;
            }
            finally { ArrayPool<char>.Shared.Return(upper); }
            if (_exactCount == _exactName.Length)
            {
                var size = _exactName.Length + Math.Max(64, _exactName.Length / 2);
                Array.Resize(ref _exactName, size);
                Array.Resize(ref _exactOffset, size);
                Array.Resize(ref _exactLength, size);
            }
            EnsureCapacity(ref _exact, _exactUsed + original.Length);
            original.CopyTo(_exact.AsSpan(_exactUsed));
            _exactName[_exactCount] = id;
            _exactOffset[_exactCount] = _exactUsed;
            _exactLength[_exactCount++] = checked((ushort)original.Length);
            _exactUsed += original.Length;
            flags |= ExactFlag;
        }
        _pool[_poolUsed++] = 0;
        _flags[id] = flags;
        if (flags == ExactFlag) AppendPlain(id, name.ToString());
        _count++;
        if (_count > _buckets.Length) Rehash(_buckets.Length * 2);
        else Link(id, hash);
        return id;
    }

    private void AppendPlain(int id, string text)
    {
        var plain = Encoding.UTF8.GetBytes(Plain(text));
        if (plain.Length == 0 || Array.IndexOf(plain, (byte)0) >= 0 || Scanned(id).SequenceEqual(plain)) return;
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
        for (var id = 0; id < _count; id++) Link(id, Hash(Original(id), (byte)(_flags[id] & WideFlag)));
    }

    private void GrowEntries()
    {
        var size = Math.Max(256, _offset.Length + _offset.Length / 2);
        Array.Resize(ref _offset, size);
        Array.Resize(ref _length, size);
        Array.Resize(ref _flags, size);
        Array.Resize(ref _next, size);
    }

    internal void Trim()
    {
        Array.Resize(ref _pool, _poolUsed + Math.Max(1 << 16, _poolUsed / 64));
        var size = _count + Math.Max(256, _count / 64);
        Array.Resize(ref _offset, size);
        Array.Resize(ref _length, size);
        Array.Resize(ref _flags, size);
        Array.Resize(ref _next, size);
        Array.Resize(ref _exact, _exactUsed + Math.Max(1 << 12, _exactUsed / 64));
        var exactSize = _exactCount + Math.Max(64, _exactCount / 64);
        Array.Resize(ref _exactName, exactSize);
        Array.Resize(ref _exactOffset, exactSize);
        Array.Resize(ref _exactLength, exactSize);
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

    internal static byte Upper(byte value) => (uint)(value - 'a') <= 25 ? (byte)(value - 32) : value;

    private static bool IsUpperLetter(byte value) => (uint)(value - 'A') <= 25;

    internal static int CompareIgnoreCase(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right)
    {
        var shared = Math.Min(left.Length, right.Length);
        var at = 0;
        while (true)
        {
            at += left.Slice(at, shared - at).CommonPrefixLength(right.Slice(at, shared - at));
            if (at == shared) return left.Length.CompareTo(right.Length);
            var a = Upper(left[at]);
            var b = Upper(right[at]);
            if (a != b) return a.CompareTo(b);
            at++;
        }
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static bool MatchesAt(ref byte text, ReadOnlySpan<byte> upperPattern)
    {
        for (var i = 0; i < upperPattern.Length; i++)
        {
            var have = Unsafe.Add(ref text, i);
            var want = upperPattern[i];
            if (have == want) continue;
            if (IsUpperLetter(want) && have == (want | 0x20)) continue;
            return false;
        }
        return true;
    }

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static int IndexOfIgnoreCase(ReadOnlySpan<byte> text, ReadOnlySpan<byte> upperPattern)
    {
        var length = upperPattern.Length;
        if (length == 0) return 0;
        if (text.Length < length) return -1;
        var first = upperPattern[0];
        if (length == 1)
            return IsUpperLetter(first) ? text.IndexOfAny(first, (byte)(first | 0x20)) : text.IndexOf(first);
        var offset = length - 1;
        var second = upperPattern[offset];
        var firstFold = IsUpperLetter(first) ? (byte)0x20 : (byte)0;
        var secondFold = IsUpperLetter(second) ? (byte)0x20 : (byte)0;
        ref var start = ref MemoryMarshal.GetReference(text);
        var last = text.Length - length;
        var at = 0;
        if (Vector256.IsHardwareAccelerated && last >= Vector256<byte>.Count - 1)
        {
            var firstMask = Vector256.Create(firstFold);
            var secondMask = Vector256.Create(secondFold);
            var firstTarget = Vector256.Create((byte)(first | firstFold));
            var secondTarget = Vector256.Create((byte)(second | secondFold));
            for (; at <= last - (Vector256<byte>.Count - 1); at += Vector256<byte>.Count)
            {
                var left = Vector256.LoadUnsafe(ref start, (nuint)at) | firstMask;
                var right = Vector256.LoadUnsafe(ref start, (nuint)(at + offset)) | secondMask;
                var hits = (Vector256.Equals(left, firstTarget) & Vector256.Equals(right, secondTarget)).ExtractMostSignificantBits();
                while (hits != 0)
                {
                    var position = at + BitOperations.TrailingZeroCount(hits);
                    if (MatchesAt(ref Unsafe.Add(ref start, position), upperPattern)) return position;
                    hits &= hits - 1;
                }
            }
        }
        else if (Vector128.IsHardwareAccelerated && last >= Vector128<byte>.Count - 1)
        {
            var firstMask = Vector128.Create(firstFold);
            var secondMask = Vector128.Create(secondFold);
            var firstTarget = Vector128.Create((byte)(first | firstFold));
            var secondTarget = Vector128.Create((byte)(second | secondFold));
            for (; at <= last - (Vector128<byte>.Count - 1); at += Vector128<byte>.Count)
            {
                var left = Vector128.LoadUnsafe(ref start, (nuint)at) | firstMask;
                var right = Vector128.LoadUnsafe(ref start, (nuint)(at + offset)) | secondMask;
                var hits = (Vector128.Equals(left, firstTarget) & Vector128.Equals(right, secondTarget)).ExtractMostSignificantBits();
                while (hits != 0)
                {
                    var position = at + BitOperations.TrailingZeroCount(hits);
                    if (MatchesAt(ref Unsafe.Add(ref start, position), upperPattern)) return position;
                    hits &= hits - 1;
                }
            }
        }
        for (; at <= last; at++)
            if (MatchesAt(ref Unsafe.Add(ref start, at), upperPattern)) return at;
        return -1;
    }

    internal ulong[] Match(byte[] pattern, CancellationToken cancellationToken)
    {
        var bits = new ulong[(_count + 63) >> 6];
        Scan(_pool, _poolUsed, _offset, null, _count, pattern, false, bits, cancellationToken);
        return bits;
    }

    internal void MatchLiteralRemainder(byte[] pattern, ulong[] masks, int wordStartOffset, CancellationToken cancellationToken) =>
        Scan(_pool, _poolUsed, _offset, null, _count, pattern, false, masks, cancellationToken, wordStartOffset);

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    internal static bool HasLiteralWordStart(ReadOnlySpan<byte> name, ReadOnlySpan<byte> pattern, int matchedAt)
    {
        while (matchedAt > 0)
        {
            if (name[matchedAt - 1] is (byte)' ' or (byte)'_' or (byte)'-' or (byte)'.') return true;
            if (matchedAt == name.Length - pattern.Length) return false;
            var next = IndexOfIgnoreCase(name[(matchedAt + 1)..], pattern);
            if (next < 0) return false;
            matchedAt += next + 1;
        }
        return false;
    }

    internal ulong[] Find(byte[] upper, byte[] plain, bool wholeWord, CancellationToken cancellationToken)
    {
        var bits = new ulong[(_count + 63) >> 6];
        Scan(_pool, _poolUsed, _offset, null, _count, upper, wholeWord, bits, cancellationToken);
        if (!plain.AsSpan().SequenceEqual(upper))
            Scan(_pool, _poolUsed, _offset, null, _count, plain, wholeWord, bits, cancellationToken);
        Scan(_plain, _plainUsed, _plainOffset, _plainName, _plainCount, plain, wholeWord, bits, cancellationToken);
        return bits;
    }

    private static void Scan(byte[] pool, int used, int[] offsets, int[]? owners, int count, byte[] pattern, bool wholeWord,
        ulong[] bits, CancellationToken cancellationToken, int wordStartOffset = 0)
    {
        if (pattern.Length == 0 || count == 0 || Array.IndexOf(pattern, (byte)0) >= 0) return;
        var chunks = used < (1 << 20) ? 1 : Math.Clamp(Environment.ProcessorCount, 1, 16);
        Parallel.For(0, chunks, new ParallelOptions { CancellationToken = cancellationToken },
            [MethodImpl(MethodImplOptions.AggressiveOptimization)] (int chunk) =>
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
            var entry = 0;
            while (at < limit)
            {
                if ((++steps & 1023) == 0) cancellationToken.ThrowIfCancellationRequested();
                var found = IndexOfIgnoreCase(pool.AsSpan(at, limit - at), pattern);
                if (found < 0) break;
                var position = at + found;
                entry = EntryAt(offsets, count, entry, position);
                var entryEnd = entry + 1 < count ? offsets[entry + 1] - 1 : used - 1;
                if (wholeWord && !IsWholeWord(pool, offsets[entry], entryEnd, position, pattern.Length))
                {
                    at = position + 1;
                    continue;
                }
                at = entryEnd + 1;
                var maskOffset = 0;
                if (wordStartOffset != 0)
                {
                    var entryStart = offsets[entry];
                    if (position == entryStart) continue;
                    if (HasLiteralWordStart(pool.AsSpan(entryStart, entryEnd - entryStart), pattern, position - entryStart))
                        maskOffset = wordStartOffset;
                }
                var id = owners is null ? entry : owners[entry];
                Interlocked.Or(ref bits[maskOffset + (id >> 6)], 1UL << (id & 63));
            }
        });
    }

    private static bool IsWholeWord(byte[] pool, int entryStart, int entryEnd, int position, int length) =>
        (position == entryStart || WordBreaks.Contains(pool[position - 1])) &&
        (position + length == entryEnd || WordBreaks.Contains(pool[position + length]));

    [MethodImpl(MethodImplOptions.AggressiveOptimization)]
    private static int EntryAt(int[] offsets, int count, int from, int position)
    {
        var lo = from;
        var step = 1;
        while (step < count - lo && offsets[lo + step] <= position)
        {
            lo += step;
            step <<= 1;
        }
        var hi = Math.Min(lo + step, count) - 1;
        while (lo < hi)
        {
            var mid = (lo + hi + 1) >>> 1;
            if (offsets[mid] <= position) lo = mid; else hi = mid - 1;
        }
        return lo;
    }

    internal ReadOnlySpan<byte> Extension(int id)
    {
        var name = Scanned(id);
        var dot = name.LastIndexOf((byte)'.');
        return dot < 0 || dot == name.Length - 1 ? [] : name[dot..];
    }

    internal void Write(BinaryWriter writer)
    {
        writer.Write(_count);
        writer.Write(_poolUsed);
        writer.Write(_exactCount);
        writer.Write(_exactUsed);
        writer.Write(_pool, 0, _poolUsed);
        IndexArrays.Write(writer, _offset.AsSpan(0, _count));
        IndexArrays.Write(writer, _length.AsSpan(0, _count));
        writer.Write(_flags, 0, _count);
        IndexArrays.Write(writer, _exactName.AsSpan(0, _exactCount));
        IndexArrays.Write(writer, _exactOffset.AsSpan(0, _exactCount));
        IndexArrays.Write(writer, _exactLength.AsSpan(0, _exactCount));
        writer.Write(_exact, 0, _exactUsed);
    }

    internal static IndexNames Read(BinaryReader reader)
    {
        var names = ReadArrays(reader);
        names.Complete();
        return names;
    }

    internal static IndexNames ReadArrays(BinaryReader reader)
    {
        var names = new IndexNames();
        var count = reader.ReadInt32();
        var poolUsed = reader.ReadInt32();
        var exactCount = reader.ReadInt32();
        var exactUsed = reader.ReadInt32();
        if (count < 0 || poolUsed < 0 || exactCount < 0 || exactUsed < 0 || exactCount > count)
            throw new InvalidDataException("Ad havuzu başlığı geçersiz.");
        names._pool = IndexArrays.ReadBytes(reader, poolUsed, poolUsed + Math.Max(1 << 16, poolUsed / 64));
        names._poolUsed = poolUsed;
        var capacity = count + Math.Max(256, count / 64);
        names._offset = IndexArrays.Read<int>(reader, count, capacity);
        names._length = IndexArrays.Read<ushort>(reader, count, capacity);
        names._flags = IndexArrays.ReadBytes(reader, count, capacity);
        var exactCapacity = exactCount + Math.Max(64, exactCount / 64);
        names._exactName = IndexArrays.Read<int>(reader, exactCount, exactCapacity);
        names._exactOffset = IndexArrays.Read<int>(reader, exactCount, exactCapacity);
        names._exactLength = IndexArrays.Read<ushort>(reader, exactCount, exactCapacity);
        names._exact = IndexArrays.ReadBytes(reader, exactUsed, exactUsed + Math.Max(1 << 12, exactUsed / 64));
        names._exactCount = exactCount;
        names._exactUsed = exactUsed;
        names._next = new int[capacity];
        names._count = count;
        return names;
    }

    internal void Complete()
    {
        var expected = 0L;
        var exact = 0;
        for (var id = 0; id < _count; id++)
        {
            if (_offset[id] != expected ||
                (long)_offset[id] + _length[id] >= _poolUsed ||
                _pool[_offset[id] + _length[id]] != 0 ||
                (_flags[id] & ~(WideFlag | ExactFlag)) != 0 ||
                (_flags[id] & WideFlag) != 0 && (_flags[id] & ExactFlag) == 0)
                throw new InvalidDataException("Ad havuzu kaydı geçersiz.");
            expected = _offset[id] + _length[id] + 1L;
            if ((_flags[id] & ExactFlag) == 0) continue;
            if (exact == _exactCount || _exactName[exact] != id ||
                _exactOffset[exact] < 0 || (long)_exactOffset[exact] + _exactLength[exact] > _exactUsed)
                throw new InvalidDataException("Ad havuzu özgün yazım kaydı geçersiz.");
            exact++;
        }
        if (expected != _poolUsed || exact != _exactCount) throw new InvalidDataException("Ad havuzu uzunluğu geçersiz.");
        var buckets = 1024;
        while (buckets < _count) buckets *= 2;
        Rehash(buckets);
        for (var at = 0; at < _exactCount; at++)
        {
            var id = _exactName[at];
            if (!IsWide(id)) AppendPlain(id, Text(id));
        }
    }
}
