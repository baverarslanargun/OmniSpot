using System.Buffers;
using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace SmartFileLauncher.Core.Index;

public static partial class IndexEntryCodec
{
    internal const int BinaryEntryHeaderBytes = 46;

    internal static void WriteBinary(IBufferWriter<byte> writer, IndexEntry entry)
    {
        if (entry.Name.Length == 0 || entry.Name.Length > (WideName - 1) / 3)
            throw new InvalidDataException("İndeks kaydı adı uzunluğu geçersiz.");
        var span = writer.GetSpan(BinaryEntryHeaderBytes + entry.Name.Length * 3);
        BinaryPrimitives.WriteUInt64LittleEndian(span, entry.FileReference);
        BinaryPrimitives.WriteUInt64LittleEndian(span[8..], entry.ParentReference);
        BinaryPrimitives.WriteUInt32LittleEndian(span[16..], (uint)entry.Attributes);
        BinaryPrimitives.WriteInt64LittleEndian(span[20..], entry.SizeBytes);
        BinaryPrimitives.WriteInt64LittleEndian(span[28..], entry.LastWriteTimeUtc);
        BinaryPrimitives.WriteInt64LittleEndian(span[36..], entry.CreatedTimeUtc);
        var name = span[BinaryEntryHeaderBytes..];
        ushort header;
        int written;
        if (Utf8.FromUtf16(entry.Name, name, out _, out written, replaceInvalidSequences: false) == OperationStatus.Done)
            header = checked((ushort)written);
        else
        {
            var wide = MemoryMarshal.AsBytes(entry.Name.AsSpan());
            wide.CopyTo(name);
            written = wide.Length;
            header = (ushort)(written | WideName);
        }
        BinaryPrimitives.WriteUInt16LittleEndian(span[44..], header);
        writer.Advance(BinaryEntryHeaderBytes + written);
    }

    internal static IEnumerable<IndexEntry> ReadBinaryEntries(ReadOnlyMemory<byte> payload, int expectedCount)
    {
        if (expectedCount < 0 || expectedCount > payload.Length / BinaryEntryHeaderBytes)
            throw new InvalidDataException("İndeks sayfası kayıt sayısı geçersiz.");
        var offset = 0;
        var count = 0;
        while (offset < payload.Length)
        {
            if (count >= expectedCount) throw new InvalidDataException("İndeks sayfasında fazla kayıt var.");
            var entry = ReadBinary(payload.Span[offset..], out var consumed);
            offset += consumed;
            count++;
            yield return entry;
        }
        if (count != expectedCount) throw new InvalidDataException("İndeks sayfasında eksik kayıt var.");
    }

    private static IndexEntry ReadBinary(ReadOnlySpan<byte> span, out int consumed)
    {
        if (span.Length < BinaryEntryHeaderBytes) throw new InvalidDataException("İndeks kaydı başlığı eksik.");
        var header = BinaryPrimitives.ReadUInt16LittleEndian(span[44..]);
        var length = header & ~WideName;
        var wide = (header & WideName) != 0;
        if (length == 0 || length > span.Length - BinaryEntryHeaderBytes || (wide && (length & 1) != 0))
            throw new InvalidDataException("İndeks kaydı adı eksik veya geçersiz.");
        var bytes = span.Slice(BinaryEntryHeaderBytes, length);
        var name = wide ? new string(MemoryMarshal.Cast<byte, char>(bytes)) : Encoding.UTF8.GetString(bytes);
        consumed = BinaryEntryHeaderBytes + length;
        return new IndexEntry(BinaryPrimitives.ReadUInt64LittleEndian(span),
            BinaryPrimitives.ReadUInt64LittleEndian(span[8..]), name,
            (FileAttributes)BinaryPrimitives.ReadUInt32LittleEndian(span[16..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[20..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[28..]),
            BinaryPrimitives.ReadInt64LittleEndian(span[36..]));
    }
}
