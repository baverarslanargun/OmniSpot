using System.Buffers.Binary;
using System.Buffers;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SmartFileLauncher.Core.Search;

internal sealed unsafe class LivePages : Stream
{
    internal const int PageSize = 1 << 18;
    private readonly string _directory, _name;
    private readonly bool _readOnly;
    private readonly HashSet<int>? _privatePages;
    private readonly List<Page> _pages = [];
    private int _length, _position;
    private bool _disposed;

    private sealed class Page : IDisposable
    {
        private readonly FileStream _file;
        private readonly MemoryMappedFile _map;
        private readonly MemoryMappedViewAccessor _view;
        internal byte* Pointer;
        internal readonly string Path;
        private int _references = 1;
        private bool _retired;
        private string? _hash;
        private int _closed;
        private bool _memoryPressure;
        internal Page(string path, bool create)
        {
            Path = path;
            _file = new(path, create ? FileMode.CreateNew : FileMode.Open,
                create ? FileAccess.ReadWrite : FileAccess.Read, create ? FileShare.Read | FileShare.Delete : FileShare.ReadWrite | FileShare.Delete);
            try
            {
                if (create) _file.SetLength(PageSize);
                if (_file.Length != PageSize) throw new InvalidDataException("Live sayfa boyutu uyuşmuyor.");
                var access = create ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read;
                _map = MemoryMappedFile.CreateFromFile(_file, null, PageSize, access, HandleInheritability.None, true);
                _view = _map.CreateViewAccessor(0, PageSize, access);
                _view.SafeMemoryMappedViewHandle.AcquirePointer(ref Pointer);
                GC.AddMemoryPressure(PageSize); _memoryPressure = true;
            }
            catch { _view?.Dispose(); _map?.Dispose(); _file.Dispose(); throw; }
        }
        internal void Flush() { _view.Flush(); _file.Flush(true); }
        internal void Retain() => Interlocked.Increment(ref _references);
        internal void Release() { if (Interlocked.Decrement(ref _references) == 0) Dispose(); }
        internal void Retire() => _retired = true;
        internal string Hash() => _hash ??= Convert.ToHexString(SHA256.HashData(new ReadOnlySpan<byte>(Pointer, PageSize)));
        public void Dispose()
        {
            if (Interlocked.Exchange(ref _closed, 1) != 0) return;
            if (Pointer != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); Pointer = null; }
            _view?.Dispose(); _map?.Dispose(); _file?.Dispose(); GC.SuppressFinalize(this);
            if (_memoryPressure) { GC.RemoveMemoryPressure(PageSize); _memoryPressure = false; }
            if (_retired) { try { File.Delete(Path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }
        }
        ~Page() => Dispose();
    }
    internal sealed record PageReference(string File, string Sha256);
    private LivePages(LivePages source, bool writable)
    {
        _directory = source._directory; _name = source._name; _readOnly = !writable;
        _length = source._length; _position = _length;
        _privatePages = writable ? [] : null;
        foreach (var page in source._pages) { page.Retain(); _pages.Add(page); }
    }
    internal LivePages Fork(bool writable) => new(this, writable);
    internal LivePages(string directory, string name, int length, PageReference[] pages)
    {
        _directory = directory; _name = name; _readOnly = true; _length = length;
        if (length < 0 || pages.Length != (length + (long)PageSize - 1) / PageSize) throw new InvalidDataException("Live sayfa listesi uyuşmuyor.");
        try
        {
            foreach (var reference in pages)
            {
                if (System.IO.Path.GetFileName(reference.File) != reference.File || !reference.File.EndsWith(".page", StringComparison.Ordinal)) throw new InvalidDataException("Live sayfa yolu geçersiz.");
                var page = new Page(System.IO.Path.Combine(directory, reference.File), false); _pages.Add(page);
                if (page.Hash() != reference.Sha256) throw new InvalidDataException("Live sayfa checksum uyuşmuyor.");
            }
        }
        catch { Dispose(); throw; }
    }
    internal PageReference[] References() => _pages.Select(page => new PageReference(System.IO.Path.GetFileName(page.Path), page.Hash())).ToArray();
    internal void RetireReplacedPages(LivePages next)
    {
        for (var index = 0; index < _pages.Count; index++)
            if (index >= next._pages.Count || !ReferenceEquals(_pages[index], next._pages[index])) _pages[index].Retire();
    }
    internal void AbandonPrivatePages() { if (_privatePages is not null) foreach (var id in _privatePages) _pages[id].Retire(); }
    private Page WritablePage(int index)
    {
        if (_privatePages is null || _privatePages.Contains(index)) return _pages[index];
        var previous = _pages[index];
        var page = new Page(System.IO.Path.Combine(_directory, $"{_name}-{Guid.NewGuid():N}.page"), true);
        new ReadOnlySpan<byte>(previous.Pointer, PageSize).CopyTo(new Span<byte>(page.Pointer, PageSize));
        _pages[index] = page; _privatePages.Add(index); previous.Release(); return page;
    }

    internal LivePages(string directory, string name, bool readOnly = false, int length = 0)
    {
        _directory = directory; _name = name; _readOnly = readOnly;
        if (length < 0) throw new InvalidDataException("Live alan uzunluğu geçersiz.");
        if (readOnly)
        {
            try { for (var i = 0; i < (length + (long)PageSize - 1) / PageSize; i++) _pages.Add(new Page(FileName(i), false)); }
            catch { Dispose(); throw; }
            _length = length;
        }
    }
    private string FileName(int page) => Path.Combine(_directory, $"{_name}-{page:D6}.page");
    internal int Used => _length;
    internal void WriteText(ReadOnlySpan<char> text)
    {
        int length;
        try { length = PackedFormat.Utf8.GetByteCount(text); }
        catch (System.Text.EncoderFallbackException)
        {
            PackedFormat.WriteUnsigned(this, checked((((ulong)text.Length * 2 + 1) << 1) | 1));
            Write(MemoryMarshal.AsBytes(text)); return;
        }
        PackedFormat.WriteUnsigned(this, checked(((ulong)length + 1) << 1));
        if (length == 0) return;
        var offset = Allocate(length);
        if ((offset & (PageSize - 1)) + length <= PageSize)
        {
            var target = new Span<byte>(WritablePage(offset / PageSize).Pointer + (offset & (PageSize - 1)), length);
            PackedFormat.Utf8.GetBytes(text, target); GC.KeepAlive(this);
        }
        else
        {
            var buffer = ArrayPool<byte>.Shared.Rent(length);
            try { PackedFormat.Utf8.GetBytes(text, buffer); Put(offset, buffer.AsSpan(0, length)); }
            finally { ArrayPool<byte>.Shared.Return(buffer); }
        }
    }
    internal long DiskBytes => (long)_pages.Count * PageSize;
    internal int Allocate(int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_readOnly) throw new InvalidOperationException("Live alan salt okunur.");
        ArgumentOutOfRangeException.ThrowIfNegative(count);
        var next = checked(_length + count);
        while ((long)_pages.Count * PageSize < next)
        {
            var index = _pages.Count;
            _pages.Add(new Page(_privatePages is null ? FileName(index) : System.IO.Path.Combine(_directory, $"{_name}-{Guid.NewGuid():N}.page"), true));
            _privatePages?.Add(index);
        }
        var start = _length; _length = next; _position = next; return start;
    }
    private ReadOnlySpan<byte> Slice(int offset, int count)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (offset < 0 || count < 0 || (long)offset + count > _length || (offset & (PageSize - 1)) + count > PageSize)
            throw new InvalidDataException("Live sayfa sınırı geçersiz.");
        return new(_pages[offset / PageSize].Pointer + (offset & (PageSize - 1)), count);
    }
    internal byte At(int offset) { var value = Slice(offset, 1)[0]; GC.KeepAlive(this); return value; }
    internal int Int32(int offset)
    {
        if ((offset & (PageSize - 1)) <= PageSize - 4)
        { var value = BinaryPrimitives.ReadInt32LittleEndian(Slice(offset, 4)); GC.KeepAlive(this); return value; }
        Span<byte> bytes = stackalloc byte[4]; Copy(offset, bytes); return BinaryPrimitives.ReadInt32LittleEndian(bytes);
    }
    internal long Int64(int offset)
    {
        if ((offset & (PageSize - 1)) <= PageSize - 8)
        { var value = BinaryPrimitives.ReadInt64LittleEndian(Slice(offset, 8)); GC.KeepAlive(this); return value; }
        Span<byte> bytes = stackalloc byte[8]; Copy(offset, bytes); return BinaryPrimitives.ReadInt64LittleEndian(bytes);
    }
    internal void PutInt32(int offset, int value)
    { Span<byte> bytes = stackalloc byte[4]; BinaryPrimitives.WriteInt32LittleEndian(bytes, value); Put(offset, bytes); }
    internal void Put(int offset, ReadOnlySpan<byte> bytes)
    {
        if (_readOnly) throw new InvalidOperationException("Live alan salt okunur.");
        if (offset < 0 || (long)offset + bytes.Length > _length) throw new InvalidDataException("Live yazma sınırı geçersiz.");
        while (!bytes.IsEmpty)
        {
            var count = Math.Min(bytes.Length, PageSize - (offset & (PageSize - 1)));
            var target = new Span<byte>(WritablePage(offset / PageSize).Pointer + (offset & (PageSize - 1)), count);
            bytes[..count].CopyTo(target); bytes = bytes[count..]; offset += count;
        }
        GC.KeepAlive(this);
    }
    internal void Copy(int offset, Span<byte> target)
    {
        if (offset < 0 || (long)offset + target.Length > _length) throw new InvalidDataException("Live okuma sınırı geçersiz.");
        while (!target.IsEmpty)
        {
            var count = Math.Min(target.Length, PageSize - (offset & (PageSize - 1)));
            Slice(offset, count).CopyTo(target); target = target[count..]; offset += count;
        }
        GC.KeepAlive(this);
    }
    internal ulong Unsigned(ref int offset, int end)
    {
        ulong value = 0;
        for (var shift = 0; shift <= 63; shift += 7)
        {
            if (offset >= end) throw new InvalidDataException("Live tamsayı eksik.");
            var part = At(offset++);
            if (shift == 63 && part > 1) throw new InvalidDataException("Live tamsayı taşması.");
            value |= (ulong)(part & 127) << shift;
            if ((part & 128) == 0) return value;
        }
        throw new InvalidDataException("Live tamsayı geçersiz.");
    }
    internal (int Offset, int Bytes, bool Utf16, bool Null) TextRange(ref int offset, int end)
    {
        var tag = Unsigned(ref offset, end);
        if (tag == 0) return (offset, 0, false, true);
        var bytes = checked((int)((tag >> 1) - 1));
        if ((long)offset + bytes > end || ((tag & 1) != 0 && (bytes & 1) != 0)) throw new InvalidDataException("Live metin eksik.");
        var range = (offset, bytes, (tag & 1) != 0, false); offset += bytes; return range;
    }
    internal string? Text(ref int offset, int end)
    {
        var range = TextRange(ref offset, end);
        if (range.Null) return null;
        if (range.Bytes == 0) return "";
        if ((range.Offset & (PageSize - 1)) + range.Bytes <= PageSize)
        {
            var span = Slice(range.Offset, range.Bytes);
            var value = range.Utf16 ? new string(MemoryMarshal.Cast<byte, char>(span)) : PackedFormat.Utf8.GetString(span);
            GC.KeepAlive(this); return value;
        }
        var bytes = new byte[range.Bytes]; Copy(range.Offset, bytes);
        return range.Utf16 ? new string(MemoryMarshal.Cast<byte, char>(bytes)) : PackedFormat.Utf8.GetString(bytes);
    }
    internal int DecodeText(int offset, Span<char> target)
    {
        var range = TextRange(ref offset, Used);
        if (range.Null) throw new InvalidDataException("Live metin yok.");
        if (range.Bytes == 0) return 0;
        byte[]? copy = null;
        ReadOnlySpan<byte> bytes;
        if ((range.Offset & (PageSize - 1)) + range.Bytes <= PageSize) bytes = Slice(range.Offset, range.Bytes);
        else { copy = new byte[range.Bytes]; Copy(range.Offset, copy); bytes = copy; }
        int count;
        if (range.Utf16) { var chars = MemoryMarshal.Cast<byte, char>(bytes); chars.CopyTo(target); count = chars.Length; }
        else count = PackedFormat.Utf8.GetChars(bytes, target);
        GC.KeepAlive(this); return count;
    }
    internal int TextLength(int offset)
    {
        var range = TextRange(ref offset, Used);
        if (range.Null) throw new InvalidDataException("Live ad eksik.");
        if (range.Utf16) return range.Bytes / 2;
        if (range.Bytes == 0) return 0;
        if ((range.Offset & (PageSize - 1)) + range.Bytes <= PageSize)
        { var count = PackedFormat.Utf8.GetCharCount(Slice(range.Offset, range.Bytes)); GC.KeepAlive(this); return count; }
        var bytes = new byte[range.Bytes]; Copy(range.Offset, bytes); return PackedFormat.Utf8.GetCharCount(bytes);
    }
    internal bool TryMatchAscii(int offset, byte[] query, int? distance, int[]? scratch, out bool matches)
    {
        matches = false;
        var range = TextRange(ref offset, Used);
        if (range.Utf16 || range.Null || (range.Offset & (PageSize - 1)) + range.Bytes > PageSize) return false;
        var bytes = range.Bytes == 0 ? ReadOnlySpan<byte>.Empty : Slice(range.Offset, range.Bytes);
        if (!Ascii.IsValid(bytes)) { GC.KeepAlive(this); return false; }
        if (distance is int max)
        {
            if (query.Length != 0 && bytes.Length != 0 && max >= 0 && Math.Abs(query.Length - bytes.Length) <= max)
                matches = MatchesAsciiDistance(query, bytes, max, scratch!);
        }
        else if (query.Length == 0) matches = true;
        else
        {
            var first = query[0]; var other = first is >= 65 and <= 90 ? (byte)(first + 32) : first is >= 97 and <= 122 ? (byte)(first - 32) : first;
            while (bytes.Length >= query.Length)
            {
                var found = bytes.IndexOfAny(first, other);
                if (found < 0 || bytes.Length - found < query.Length) break;
                bytes = bytes[found..];
                if (Ascii.EqualsIgnoreCase(bytes[..query.Length], query)) { matches = true; break; }
                bytes = bytes[1..];
            }
        }
        GC.KeepAlive(this); return true;
    }
    private static bool MatchesAsciiDistance(ReadOnlySpan<byte> source, ReadOnlySpan<byte> target, int maximum, int[] scratch)
    {
        var width = target.Length + 1; var previous = scratch.AsSpan(0, width); var current = scratch.AsSpan(width, width);
        for (var j = 0; j < width; j++) previous[j] = j;
        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i; var minimum = i;
            for (var j = 1; j < width; j++)
            {
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1));
                minimum = Math.Min(minimum, current[j]);
            }
            if (minimum > maximum) return false;
            var swap = previous; previous = current; current = swap;
        }
        return previous[target.Length] <= maximum;
    }
    internal string Hash()
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        for (var offset = 0; offset < _length;)
        { var count = Math.Min(PageSize, _length - offset); hash.AppendData(Slice(offset, count)); offset += count; }
        GC.KeepAlive(this); return Convert.ToHexString(hash.GetHashAndReset());
    }
    internal void FlushDurable()
    {
        if (_readOnly) return;
        if (_privatePages is null) foreach (var page in _pages) page.Flush();
        else foreach (var id in _privatePages) _pages[id].Flush();
    }
    public override void Flush() { }
    public override bool CanRead => true;
    public override bool CanSeek => true;
    public override bool CanWrite => !_readOnly;
    public override long Length => _length;
    public override long Position { get => _position; set => _position = checked((int)value); }
    public override long Seek(long offset, SeekOrigin origin) => Position = origin switch
    { SeekOrigin.Begin => offset, SeekOrigin.Current => Position + offset, SeekOrigin.End => Length + offset, _ => throw new ArgumentOutOfRangeException(nameof(origin)) };
    public override void SetLength(long value) => throw new NotSupportedException();
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer)
    { var count = Math.Min(buffer.Length, _length - _position); Copy(_position, buffer[..count]); _position += count; return count; }
    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> bytes)
    {
        var position = _position;
        var next = checked(position + bytes.Length);
        if (next > _length) Allocate(next - _length);
        Put(position, bytes); _position = next;
    }
    public override void WriteByte(byte value) { Span<byte> bytes = stackalloc byte[1]; bytes[0] = value; Write(bytes); }
    protected override void Dispose(bool disposing)
    {
        if (_disposed) return;
        _disposed = true; foreach (var page in _pages) page.Release(); _pages.Clear(); base.Dispose(disposing);
        GC.SuppressFinalize(this);
    }
    ~LivePages() => Dispose(false);
}
