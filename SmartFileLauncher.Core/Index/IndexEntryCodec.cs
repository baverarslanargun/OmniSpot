using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;

namespace SmartFileLauncher.Core.Index;

public static class IndexEntryCodec
{
    private const ushort WideName = 0x8000;

    public static void Write(BinaryWriter writer, IndexEntry entry)
    {
        writer.Write(entry.FileReference);
        writer.Write(entry.ParentReference);
        writer.Write((uint)entry.Attributes);
        writer.Write(entry.SizeBytes);
        writer.Write(entry.LastWriteTimeUtc);
        writer.Write(entry.CreatedTimeUtc);
        var buffer = ArrayPool<byte>.Shared.Rent(entry.Name.Length * 3 + 2);
        try
        {
            if (Utf8.FromUtf16(entry.Name, buffer, out _, out var written, replaceInvalidSequences: false) == OperationStatus.Done)
            {
                writer.Write(checked((ushort)written));
                writer.Write(buffer, 0, written);
            }
            else
            {
                var wide = MemoryMarshal.AsBytes(entry.Name.AsSpan());
                writer.Write((ushort)(checked((ushort)wide.Length) | WideName));
                writer.Write(wide);
            }
        }
        finally { ArrayPool<byte>.Shared.Return(buffer); }
    }

    public static IndexEntry Read(BinaryReader reader)
    {
        var reference = reader.ReadUInt64();
        var parent = reader.ReadUInt64();
        var attributes = (FileAttributes)reader.ReadUInt32();
        var size = reader.ReadInt64();
        var modified = reader.ReadInt64();
        var created = reader.ReadInt64();
        var header = reader.ReadUInt16();
        var length = header & ~WideName;
        var bytes = reader.ReadBytes(length);
        if (bytes.Length != length) throw new InvalidDataException("İndeks kaydı adı eksik.");
        var name = (header & WideName) != 0 ? new string(MemoryMarshal.Cast<byte, char>(bytes)) : Encoding.UTF8.GetString(bytes);
        if (name.Length == 0) throw new InvalidDataException("İndeks kaydı adı boş.");
        return new IndexEntry(reference, parent, name, attributes, size, modified, created);
    }

    public static string PackEntries(IEnumerable<IndexEntry> entries)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            foreach (var entry in entries) Write(writer, entry);
        return Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
    }

    public static List<IndexEntry> UnpackEntries(string? packed)
    {
        var result = new List<IndexEntry>();
        if (string.IsNullOrEmpty(packed)) return result;
        using var stream = new MemoryStream(Convert.FromBase64String(packed));
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        while (stream.Position < stream.Length) result.Add(Read(reader));
        return result;
    }

    public static string PackStates(IEnumerable<IndexRecordState> states)
    {
        using var stream = new MemoryStream();
        using (var writer = new BinaryWriter(stream, Encoding.UTF8, leaveOpen: true))
            foreach (var state in states)
            {
                writer.Write(state.FileReference);
                writer.Write(checked((ushort)state.Links.Count));
                foreach (var link in state.Links) Write(writer, link);
            }
        return Convert.ToBase64String(stream.GetBuffer(), 0, (int)stream.Length);
    }

    public static List<IndexRecordState> UnpackStates(string? packed)
    {
        var result = new List<IndexRecordState>();
        if (string.IsNullOrEmpty(packed)) return result;
        using var stream = new MemoryStream(Convert.FromBase64String(packed));
        using var reader = new BinaryReader(stream, Encoding.UTF8);
        while (stream.Position < stream.Length)
        {
            var reference = reader.ReadUInt64();
            var count = reader.ReadUInt16();
            var links = new List<IndexEntry>(count);
            for (var i = 0; i < count; i++) links.Add(Read(reader));
            result.Add(new IndexRecordState(reference, links));
        }
        return result;
    }
}
