using System.Collections.Immutable;
using System.Runtime.InteropServices;

namespace SmartFileLauncher.Core.Search;

internal sealed partial class CompactSearchState
{
    internal void WriteCheckpointBase(string path) => _catalog.WriteNew(path);

    internal byte[] ExportCheckpointDelta()
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream);
        writer.Write(DeltaCapacity);
        writer.Write(_nextId);
        writer.Write(_delta.Count);
        foreach (var (path, entry) in _delta.OrderBy(pair => pair.Value.Id))
        {
            WriteText(writer, path);
            writer.Write(entry.Id);
            writer.Write(entry.Item is not null);
            if (entry.Item is { } item)
            {
                WriteText(writer, item.Name);
                WriteText(writer, item.FullPath);
                writer.Write(item.IsDirectory);
                writer.Write(item.SizeBytes.HasValue);
                if (item.SizeBytes is long size) writer.Write(size);
                WriteDate(writer, item.CreatedTime);
                WriteDate(writer, item.LastWriteTime);
                writer.Write(item.OpenCount);
                WriteText(writer, item.ParentPath);
            }
            writer.Write(entry.Tokens.Length);
            foreach (var token in entry.Tokens) WriteText(writer, token);
        }
        return stream.ToArray();
    }

    internal CompactSearchState RestoreCheckpointDelta(byte[] data)
    {
        using var stream = new MemoryStream(data, writable: false);
        using var reader = new BinaryReader(stream);
        var capacity = reader.ReadInt32();
        var nextId = reader.ReadInt32();
        var count = reader.ReadInt32();
        if (capacity < 1 || count < 0 || count > capacity || nextId < _catalog.IdCapacity)
            throw new InvalidDataException("Katalog değişiklik sayısı geçersiz.");
        var state = this;
        var changes = ImmutableDictionary.CreateBuilder<string, Entry>(Comparer);
        for (var index = 0; index < count; index++)
        {
            var path = ReadText(reader) ?? throw new InvalidDataException("Katalog yolu eksik.");
            var id = reader.ReadInt32();
            if (id < 0 || id >= nextId) throw new InvalidDataException("Katalog kimliği geçersiz.");
            SearchItem? item = null;
            if (reader.ReadBoolean())
            {
                var name = ReadText(reader) ?? throw new InvalidDataException("Katalog adı eksik.");
                var fullPath = ReadText(reader) ?? throw new InvalidDataException("Katalog yolu eksik.");
                var directory = reader.ReadBoolean();
                long? size = reader.ReadBoolean() ? reader.ReadInt64() : null;
                item = new SearchItem(name, fullPath, directory, size,
                    ReadDate(reader), ReadDate(reader), reader.ReadInt32(), ReadText(reader));
                if (!Comparer.Equals(path, fullPath)) throw new InvalidDataException("Katalog yolu uyuşmuyor.");
            }
            var tokenCount = reader.ReadInt32();
            if (tokenCount < 0 || tokenCount > stream.Length - stream.Position)
                throw new InvalidDataException("Katalog token sayısı geçersiz.");
            var tokens = new string[tokenCount];
            for (var token = 0; token < tokens.Length; token++)
                tokens[token] = ReadText(reader) ?? throw new InvalidDataException("Katalog tokenı eksik.");
            changes.Add(path, new Entry(item, tokens, id));
            state = state.Change(path, item, tokens);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Katalog değişiklik kaydı geçersiz.");
        return new(_catalog, changes.ToImmutable(), state._suppressed, state._postings, state._children,
            state.ItemCount, state.TokenCount, state._missingParents, nextId, capacity, _varint, Generation);
    }

    private static void WriteText(BinaryWriter writer, string? value)
    {
        writer.Write(value?.Length ?? -1);
        if (value is not null) writer.Write(MemoryMarshal.AsBytes(value.AsSpan()));
    }

    private static string? ReadText(BinaryReader reader)
    {
        var length = reader.ReadInt32();
        if (length == -1) return null;
        if (length < 0 || (long)length * 2 > reader.BaseStream.Length - reader.BaseStream.Position)
            throw new InvalidDataException("Katalog metin uzunluğu geçersiz.");
        var bytes = reader.ReadBytes(checked(length * 2));
        return new string(MemoryMarshal.Cast<byte, char>(bytes));
    }

    private static void WriteDate(BinaryWriter writer, DateTime? value)
    {
        writer.Write(value.HasValue);
        if (value is not { } date) return;
        writer.Write(date.Ticks);
        writer.Write((byte)date.Kind);
    }

    private static DateTime? ReadDate(BinaryReader reader) =>
        reader.ReadBoolean() ? new DateTime(reader.ReadInt64(), (DateTimeKind)reader.ReadByte()) : null;
}
