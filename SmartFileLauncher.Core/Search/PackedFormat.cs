using System.Buffers.Binary;
using System.Globalization;
using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Text;

namespace SmartFileLauncher.Core.Search;

internal sealed record PackedRecord(SearchItem Item, long ModifiedUtc, long CreatedUtc,
    bool Hidden, bool System, long IndexedUtc = 0);
internal sealed record PackedSourcePosition(string VolumeId, ulong JournalId, long NextUsn, Guid RootGeneration);
internal sealed record PackedCheckpoint(long Sequence, PackedSourcePosition? Source, string LastBatchHash, string Contract)
{
    internal static string CurrentContract => $"packed-1|{typeof(BasicTokenizer).Module.ModuleVersionId:N}|{CultureInfo.CurrentCulture.Name}|{TimeZoneInfo.Local.ToSerializedString()}";
    internal static PackedCheckpoint Empty => new(0, null, "", CurrentContract);
}

internal static class PackedFormat
{
    internal const int HeaderSize = 96, RowSize = 16, Magic = 0x4F534349;
    internal const uint Hidden = 256, System = 512, OpenCount = 1024, ComplexPath = 2048,
        RawCreated = 4096, CreatedOverride = 8192, ModifiedOverride = 16384, IndexedOverride = 32768;
    internal static readonly UTF8Encoding Utf8 = new(false, true);

    internal static void WriteUnsigned(Stream stream, ulong value)
    {
        while (value >= 128) { stream.WriteByte((byte)(value | 128)); value >>= 7; }
        stream.WriteByte((byte)value);
    }
    internal static void WriteSigned(Stream stream, long value) => WriteUnsigned(stream, unchecked((ulong)((value << 1) ^ (value >> 63))));
    internal static long DecodeSigned(ulong value) => unchecked((long)(value >> 1) ^ -((long)value & 1));

    internal static void WriteText(Stream stream, string? value)
    {
        if (value is null) { stream.WriteByte(0); return; }
        try
        {
            var bytes = Utf8.GetBytes(value);
            WriteUnsigned(stream, checked(((ulong)bytes.Length + 1) << 1));
            stream.Write(bytes);
        }
        catch (EncoderFallbackException)
        {
            WriteUnsigned(stream, checked((((ulong)value.Length * 2 + 1) << 1) | 1));
            stream.Write(MemoryMarshal.AsBytes(value.AsSpan()));
        }
    }

    internal static DateTime RestoreDate(long utcTicks, DateTimeKind kind) => kind switch
    {
        DateTimeKind.Local => new DateTime(utcTicks, DateTimeKind.Utc).ToLocalTime(),
        _ => new DateTime(utcTicks, kind)
    };

    internal static uint WriteMetadata(BinaryWriter writer, PackedRecord record, bool complex, long indexedUtc)
    {
        var item = record.Item;
        uint flags = (uint)((item.IsDirectory ? 1 : 0) | (item.SizeBytes.HasValue ? 2 : 0) |
            (item.CreatedTime.HasValue ? 4 | ((int)item.CreatedTime.Value.Kind << 4) : 0) |
            (item.LastWriteTime.HasValue ? 8 | ((int)item.LastWriteTime.Value.Kind << 6) : 0));
        if (record.Hidden) flags |= Hidden;
        if (record.System) flags |= System;
        if (item.OpenCount != 0) flags |= OpenCount;
        if (complex) flags |= ComplexPath;
        if (record.CreatedUtc != 0) flags |= RawCreated;
        if (item.CreatedTime is { } created && RestoreDate(record.CreatedUtc, created.Kind).Ticks != created.Ticks) flags |= CreatedOverride;
        if (item.LastWriteTime is { } modified && RestoreDate(record.ModifiedUtc, modified.Kind).Ticks != modified.Ticks) flags |= ModifiedOverride;
        var indexed = record.IndexedUtc == 0 ? indexedUtc : record.IndexedUtc;
        if (indexed != indexedUtc) flags |= IndexedOverride;
        WriteUnsigned(writer.BaseStream, flags);
        writer.Write(record.ModifiedUtc);
        if ((flags & RawCreated) != 0) WriteSigned(writer.BaseStream, checked(record.CreatedUtc - record.ModifiedUtc));
        if (item.SizeBytes is long size) WriteSigned(writer.BaseStream, size);
        if ((flags & OpenCount) != 0) WriteSigned(writer.BaseStream, item.OpenCount);
        if ((flags & CreatedOverride) != 0) writer.Write(item.CreatedTime!.Value.Ticks);
        if ((flags & ModifiedOverride) != 0) writer.Write(item.LastWriteTime!.Value.Ticks);
        if ((flags & IndexedOverride) != 0) WriteSigned(writer.BaseStream, checked(indexed - indexedUtc));
        return flags;
    }
}

internal sealed unsafe class PackedMapping
{
    private readonly MemoryMappedFile _file;
    private readonly MemoryMappedViewAccessor _view;
    private byte* _pointer;
    private string? _retiredPath;
    internal int Length { get; }

    internal PackedMapping(FileStream stream, int length)
    {
        Length = length;
        _file = MemoryMappedFile.CreateFromFile(stream, null, 0, MemoryMappedFileAccess.Read, HandleInheritability.None, false);
        try
        {
            _view = _file.CreateViewAccessor(0, length, MemoryMappedFileAccess.Read);
            _view.SafeMemoryMappedViewHandle.AcquirePointer(ref _pointer);
            _pointer += _view.PointerOffset;
        }
        catch { _view?.Dispose(); _file.Dispose(); throw; }
    }

    private ReadOnlySpan<byte> Bytes(int offset, int count)
    {
        if (offset < 0 || count < 0 || (long)offset + count > Length) throw new InvalidDataException("Packed bölüm sınırı geçersiz.");
        return new ReadOnlySpan<byte>(_pointer + offset, count);
    }
    internal int Int32(int offset) { var value = BinaryPrimitives.ReadInt32LittleEndian(Bytes(offset, 4)); GC.KeepAlive(this); return value; }
    internal long Int64(int offset) { var value = BinaryPrimitives.ReadInt64LittleEndian(Bytes(offset, 8)); GC.KeepAlive(this); return value; }
    internal byte Byte(int offset) { var value = Bytes(offset, 1)[0]; GC.KeepAlive(this); return value; }
    internal void Copy(int offset, Span<byte> target) { Bytes(offset, target.Length).CopyTo(target); GC.KeepAlive(this); }
    internal ulong Unsigned(ref int offset, int end)
    {
        ulong value = 0;
        for (var shift = 0; shift <= 63; shift += 7)
        {
            if (offset >= end) throw new InvalidDataException("Packed tamsayı kesilmiş.");
            var part = Byte(offset++);
            if (shift == 63 && part > 1) throw new InvalidDataException("Packed tamsayı taşması.");
            value |= (ulong)(part & 127) << shift;
            if ((part & 128) == 0) return value;
        }
        throw new InvalidDataException("Packed tamsayı geçersiz.");
    }
    internal (int Offset, int Bytes, bool Utf16, bool Null) TextRange(ref int offset, int end)
    {
        var tag = Unsigned(ref offset, end);
        if (tag == 0) return (offset, 0, false, true);
        var count = checked((int)((tag >> 1) - 1));
        var utf16 = (tag & 1) != 0;
        if ((long)offset + count > end || utf16 && (count & 1) != 0) throw new InvalidDataException("Packed metin kesilmiş.");
        var start = offset;
        offset += count;
        return (start, count, utf16, false);
    }
    internal string? Text(ref int offset, int end)
    {
        var range = TextRange(ref offset, end);
        if (range.Null) return null;
        var span = Bytes(range.Offset, range.Bytes);
        var value = range.Utf16 ? new string(MemoryMarshal.Cast<byte, char>(span)) : PackedFormat.Utf8.GetString(span);
        GC.KeepAlive(this);
        return value;
    }
    internal int TextCharCount((int Offset, int Bytes, bool Utf16, bool Null) range)
    {
        var count = range.Utf16 ? range.Bytes / 2 : PackedFormat.Utf8.GetCharCount(Bytes(range.Offset, range.Bytes));
        GC.KeepAlive(this);
        return count;
    }
    internal int Decode((int Offset, int Bytes, bool Utf16, bool Null) range, Span<char> target)
    {
        var bytes = Bytes(range.Offset, range.Bytes);
        int count;
        if (range.Utf16) { var chars = MemoryMarshal.Cast<byte, char>(bytes); chars.CopyTo(target); count = chars.Length; }
        else count = PackedFormat.Utf8.GetChars(bytes, target);
        GC.KeepAlive(this);
        return count;
    }
    internal void CloseUnpublished()
    {
        if (_pointer != null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _pointer = null; }
        _view.Dispose(); _file.Dispose(); GC.SuppressFinalize(this);
    }
    internal void RetireFile(string path) => _retiredPath = path;
    ~PackedMapping()
    {
        if (_pointer != null) _view.SafeMemoryMappedViewHandle.ReleasePointer();
        _view?.Dispose(); _file?.Dispose();
        if (_retiredPath is { } path)
            try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
