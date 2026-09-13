using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SmartFileLauncher.Core.Search;

internal sealed class StreamingCatalogBuilder : IDisposable
{
    private readonly string _workspace;
    private readonly FileStream _rows;
    private readonly FileStream _arena;
    private readonly FileStream _itemTokens;
    private readonly Dictionary<string, int> _terms = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _names = [];
    private readonly List<long> _postings = [];
    private readonly List<long> _children = [];
    private readonly ITokenizer _tokenizer;
    private bool _finished;
    internal int Count { get; private set; }

    internal StreamingCatalogBuilder(string workspace, ITokenizer tokenizer)
    {
        _workspace = workspace;
        Directory.CreateDirectory(workspace);
        _rows = NewFile("rows.tmp");
        _arena = NewFile("arena.tmp");
        _itemTokens = NewFile("tokens.tmp");
        _tokenizer = tokenizer;
    }

    internal int Add(SearchItem item, int parentId, string? parentFullPath)
    {
        if (_finished) throw new InvalidOperationException("Katalog tamamlandı.");
        if (parentId < -1 || parentId >= Count) throw new ArgumentOutOfRangeException(nameof(parentId));
        var id = Count++;
        var prefixId = parentId >= 0 && parentFullPath is not null &&
            item.FullPath.Length > parentFullPath.Length && item.FullPath[parentFullPath.Length] == '\\' &&
            item.FullPath.AsSpan().StartsWith(parentFullPath.AsSpan(), StringComparison.Ordinal) ? parentId : -1;
        var fragment = item.FullPath.AsSpan(prefixId < 0 ? 0 : parentFullPath!.Length);
        var fragmentOffset = AppendText(fragment);
        var nameOffset = fragment.EndsWith(item.Name.AsSpan(), StringComparison.Ordinal)
            ? fragmentOffset + (fragment.Length - item.Name.Length) * 2 : AppendText(item.Name);
        var externalParent = parentId < 0 || !string.Equals(item.ParentPath, parentFullPath, StringComparison.Ordinal)
            ? item.ParentPath : null;
        var parentOffset = -1;
        if (externalParent is not null)
        {
            parentOffset = checked((int)_arena.Position);
            WriteInt(_arena, externalParent.Length);
            AppendText(externalParent);
        }
        var tokenStart = checked((int)_itemTokens.Position);
        foreach (var token in _tokenizer.Tokenize(item.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_terms.TryGetValue(token, out var term))
            {
                term = _terms.Count;
                _terms.Add(token, term);
                _names.Add(token);
            }
            WriteInt(_itemTokens, term);
            _postings.Add(((long)term << 32) | (uint)id);
        }
        if (parentId >= 0) _children.Add(((long)parentId << 32) | (uint)id);
        Span<byte> row = stackalloc byte[64];
        row.Clear();
        BinaryPrimitives.WriteInt32LittleEndian(row, nameOffset);
        BinaryPrimitives.WriteInt32LittleEndian(row[4..], item.Name.Length);
        BinaryPrimitives.WriteInt32LittleEndian(row[8..], fragmentOffset);
        BinaryPrimitives.WriteInt32LittleEndian(row[12..], fragment.Length);
        BinaryPrimitives.WriteInt32LittleEndian(row[16..], prefixId);
        BinaryPrimitives.WriteInt32LittleEndian(row[20..], parentId);
        BinaryPrimitives.WriteInt32LittleEndian(row[24..], parentOffset);
        var flags = (item.IsDirectory ? 1 : 0) | (item.SizeBytes.HasValue ? 2 : 0) |
            (item.CreatedTime.HasValue ? 4 | ((int)item.CreatedTime.Value.Kind << 4) : 0) |
            (item.LastWriteTime.HasValue ? 8 | ((int)item.LastWriteTime.Value.Kind << 6) : 0);
        BinaryPrimitives.WriteInt32LittleEndian(row[28..], flags);
        BinaryPrimitives.WriteInt64LittleEndian(row[32..], item.SizeBytes ?? 0);
        BinaryPrimitives.WriteInt64LittleEndian(row[40..], item.CreatedTime?.Ticks ?? 0);
        BinaryPrimitives.WriteInt64LittleEndian(row[48..], item.LastWriteTime?.Ticks ?? 0);
        BinaryPrimitives.WriteInt32LittleEndian(row[56..], item.OpenCount);
        BinaryPrimitives.WriteInt32LittleEndian(row[60..], tokenStart);
        _rows.Write(row);
        return id;
    }

    internal CompactSearchState Complete(string path, CancellationToken ct = default)
    {
        if (_finished) throw new InvalidOperationException("Katalog tamamlandı.");
        _finished = true;
        ct.ThrowIfCancellationRequested();
        CollectionsMarshal.AsSpan(_children).Sort();
        CollectionsMarshal.AsSpan(_postings).Sort();
        var groups = new List<(int Parent, int Start, int Count)>();
        for (var position = 0; position < _children.Count;)
        {
            var start = position;
            var parent = (int)(_children[position] >> 32);
            do { position++; } while (position < _children.Count && (int)(_children[position] >> 32) == parent);
            groups.Add((parent, start, position - start));
        }
        var termStarts = new int[_terms.Count];
        var termCounts = new int[_terms.Count];
        using var postingFile = NewFile("postings.tmp");
        var postingPosition = 0;
        for (var term = 0; term < termStarts.Length; term++)
        {
            ct.ThrowIfCancellationRequested();
            termStarts[term] = checked((int)postingFile.Position);
            var previous = 0;
            while (postingPosition < _postings.Count && (int)(_postings[postingPosition] >> 32) == term)
            {
                var id = (int)_postings[postingPosition++];
                var delta = (uint)(id - previous);
                while (delta >= 128) { postingFile.WriteByte((byte)(delta | 128)); delta >>= 7; }
                postingFile.WriteByte((byte)delta);
                previous = id;
                termCounts[term]++;
            }
        }
        var termNames = new int[_names.Count];
        for (var term = 0; term < _names.Count; term++) termNames[term] = AppendText(_names[term]);
        var termOffset = checked(56 + Count * 64);
        var childOffset = checked(termOffset + _terms.Count * 16);
        var childDataOffset = checked(childOffset + 4 + groups.Count * 12);
        var itemTokenOffset = checked(childDataOffset + _children.Count * 4);
        var postingOffset = checked(itemTokenOffset + (int)_itemTokens.Length);
        var arenaOffset = checked(postingOffset + (int)postingFile.Length);
        var length = checked(arenaOffset + (int)_arena.Length);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024))
        {
            using var writer = new BinaryWriter(output, Encoding.Unicode, leaveOpen: true);
            foreach (var field in new[] { 0x4F534349, 3, Count, _terms.Count, 56, termOffset,
                         childOffset, itemTokenOffset, postingOffset, arenaOffset, length, 1 }) writer.Write(field);
            writer.Write(0L);
            _rows.Position = 0;
            Span<byte> row = stackalloc byte[64];
            for (var id = 0; id < Count; id++)
            {
                if ((id & 4095) == 0) ct.ThrowIfCancellationRequested();
                _rows.ReadExactly(row);
                Relocate(row, 0, arenaOffset);
                Relocate(row, 8, arenaOffset);
                if (BinaryPrimitives.ReadInt32LittleEndian(row[24..]) >= 0) Relocate(row, 24, arenaOffset);
                Relocate(row, 60, itemTokenOffset);
                output.Write(row);
            }
            for (var term = 0; term < termStarts.Length; term++)
            {
                writer.Write(checked(arenaOffset + termNames[term])); writer.Write(_names[term].Length);
                writer.Write(checked(postingOffset + termStarts[term])); writer.Write(termCounts[term]);
            }
            writer.Write(groups.Count);
            foreach (var group in groups)
            {
                writer.Write(group.Parent); writer.Write(checked(childDataOffset + group.Start * 4)); writer.Write(group.Count);
            }
            foreach (var pair in _children) writer.Write((int)pair);
            Copy(_itemTokens, output); Copy(postingFile, output); Copy(_arena, output);
            if (output.Position != length) throw new InvalidDataException("Katalog uzunluğu uyuşmuyor.");
            output.Flush();
            output.Position = 0;
            var hash = SHA256.HashData(output);
            output.Position = length;
            output.Write(hash);
            output.Flush(flushToDisk: true);
        }
        return CompactSearchState.OpenMapped(path);
    }

    private FileStream NewFile(string name) => new(Path.Combine(_workspace, name), FileMode.CreateNew,
        FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);

    private int AppendText(ReadOnlySpan<char> value)
    {
        var offset = checked((int)_arena.Position);
        _arena.Write(MemoryMarshal.AsBytes(value));
        return offset;
    }

    private static void Relocate(Span<byte> row, int position, int offset) =>
        BinaryPrimitives.WriteInt32LittleEndian(row[position..], checked(BinaryPrimitives.ReadInt32LittleEndian(row[position..]) + offset));

    private static void WriteInt(Stream stream, int value)
    {
        Span<byte> buffer = stackalloc byte[4];
        BinaryPrimitives.WriteInt32LittleEndian(buffer, value);
        stream.Write(buffer);
    }

    private static void Copy(Stream source, Stream destination) { source.Position = 0; source.CopyTo(destination); }

    public void Dispose()
    {
        _rows.Dispose(); _arena.Dispose(); _itemTokens.Dispose();
        _terms.Clear(); _names.Clear(); _postings.Clear(); _children.Clear();
    }
}
