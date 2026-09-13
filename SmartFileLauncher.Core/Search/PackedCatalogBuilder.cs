using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SmartFileLauncher.Core.Search;

internal sealed class PackedCatalogBuilder : IDisposable
{
    private readonly FileStream _rows, _text, _metadata, _tokens;
    private readonly BinaryWriter _rowWriter, _metaWriter;
    private readonly string _workspace;
    private readonly Dictionary<string, int> _terms = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<string> _names = [];
    private readonly List<long> _postings = [], _children = [];
    private readonly ITokenizer _tokenizer;
    private bool _finished;
    internal int Count { get; private set; }
    internal long IndexedUtc { get; } = DateTime.UtcNow.Ticks;

    internal PackedCatalogBuilder(string workspace, ITokenizer tokenizer)
    {
        _workspace = workspace;
        Directory.CreateDirectory(workspace);
        _rows = NewFile("packed-rows.tmp"); _text = NewFile("packed-text.tmp");
        _metadata = NewFile("packed-meta.tmp"); _tokens = NewFile("packed-tokens.tmp");
        _rowWriter = new BinaryWriter(_rows, Encoding.UTF8, true);
        _metaWriter = new BinaryWriter(_metadata, Encoding.UTF8, true);
        _tokenizer = tokenizer;
    }

    internal int Add(PackedRecord record, int parentId, string? parentFullPath)
    {
        if (_finished) throw new InvalidOperationException("Packed katalog tamamlandı.");
        if (parentId < -1 || parentId >= Count) throw new ArgumentOutOfRangeException(nameof(parentId));
        var item = record.Item;
        var id = Count++;
        var complex = parentId < 0 || !string.Equals(item.ParentPath, parentFullPath, StringComparison.Ordinal) ||
            !string.Equals(item.FullPath, parentFullPath + "\\" + item.Name, StringComparison.Ordinal);
        var row = _rowWriter;
        row.Write(parentId); row.Write(checked((int)_text.Position));
        row.Write(checked((int)_metadata.Position)); row.Write(checked((int)_tokens.Position));
        PackedFormat.WriteText(_text, item.Name);
        if (complex) { PackedFormat.WriteText(_text, item.FullPath); PackedFormat.WriteText(_text, item.ParentPath); }
        PackedFormat.WriteMetadata(_metaWriter, record, complex, IndexedUtc);
        foreach (var token in _tokenizer.Tokenize(item.Name).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!_terms.TryGetValue(token, out var term))
            {
                term = _names.Count;
                _names.Add(token); _terms.Add(token, term);
            }
            PackedFormat.WriteUnsigned(_tokens, (uint)term);
            _postings.Add(((long)term << 32) | (uint)id);
        }
        if (parentId >= 0) _children.Add(((long)parentId << 32) | (uint)id);
        return id;
    }

    internal PackedCatalog Complete(string path, PackedCheckpoint? checkpoint = null,
        long generation = 0, CancellationToken ct = default)
    {
        WriteNew(path, checkpoint, generation, ct);
        return PackedCatalog.Open(path);
    }

    internal void WriteNew(string path, PackedCheckpoint? checkpoint = null,
        long generation = 0, CancellationToken ct = default)
    {
        if (_finished) throw new InvalidOperationException("Packed katalog tamamlandı.");
        _finished = true;
        checkpoint ??= PackedCheckpoint.Empty;
        ct.ThrowIfCancellationRequested();
        CollectionsMarshal.AsSpan(_children).Sort();
        CollectionsMarshal.AsSpan(_postings).Sort();
        var groups = new List<(int Parent, int Start, int Count)>();
        for (var i = 0; i < _children.Count;)
        {
            var start = i;
            var parent = (int)(_children[i] >> 32);
            do { i++; } while (i < _children.Count && (int)(_children[i] >> 32) == parent);
            groups.Add((parent, start, i - start));
        }
        var termStarts = new int[_names.Count];
        var termCounts = new int[_names.Count];
        var termNames = new int[_names.Count];
        var lookup = Enumerable.Range(0, _names.Count).ToArray();
        Array.Sort(lookup, (left, right) => StringComparer.OrdinalIgnoreCase.Compare(_names[left], _names[right]));
        using var postings = NewFile("packed-postings.tmp");
        var position = 0;
        for (var term = 0; term < _names.Count; term++)
        {
            ct.ThrowIfCancellationRequested();
            termStarts[term] = checked((int)postings.Position);
            var previous = 0;
            while (position < _postings.Count && (int)(_postings[position] >> 32) == term)
            {
                var id = (int)_postings[position++];
                PackedFormat.WriteUnsigned(postings, checked((uint)(id - previous)));
                previous = id;
                termCounts[term]++;
            }
            termNames[term] = checked((int)_text.Position);
            PackedFormat.WriteText(_text, _names[term]);
        }
        var termRows = checked(PackedFormat.HeaderSize + Count * PackedFormat.RowSize);
        var lookupOffset = checked(termRows + _names.Count * 12);
        var childOffset = checked(lookupOffset + _names.Count * 4);
        var childData = checked(childOffset + 4 + groups.Count * 12);
        var itemTokens = checked(childData + _children.Count * 4);
        var postingOffset = checked(itemTokens + (int)_tokens.Length);
        var metadataOffset = checked(postingOffset + (int)postings.Length);
        var textOffset = checked(metadataOffset + (int)_metadata.Length);
        var stateOffset = checked(textOffset + (int)_text.Length);
        var state = JsonSerializer.SerializeToUtf8Bytes(checkpoint);
        var payloadLength = checked(stateOffset + state.Length);
        using (var output = new FileStream(path, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None, 128 * 1024))
        {
            using var writer = new BinaryWriter(output, Encoding.UTF8, true);
            foreach (var field in new[] { PackedFormat.Magic, 4, Count, _names.Count, PackedFormat.HeaderSize,
                         termRows, childOffset, itemTokens, postingOffset, textOffset, payloadLength, 1 }) writer.Write(field);
            writer.Write(generation);
            writer.Write(metadataOffset); writer.Write(lookupOffset); writer.Write(stateOffset);
            writer.Write(_names.Count == 0 ? 0 : _names.Max(name => name.Length));
            writer.Write(PackedFormat.RowSize); writer.Write(0); writer.Write(IndexedUtc); writer.Write(0L);
            _rows.Position = 0;
            using var reader = new BinaryReader(_rows, Encoding.UTF8, true);
            for (var id = 0; id < Count; id++)
            {
                if ((id & 4095) == 0) ct.ThrowIfCancellationRequested();
                writer.Write(reader.ReadInt32());
                writer.Write(checked(textOffset + reader.ReadInt32()));
                writer.Write(checked(metadataOffset + reader.ReadInt32()));
                writer.Write(checked(itemTokens + reader.ReadInt32()));
            }
            for (var term = 0; term < _names.Count; term++)
            {
                writer.Write(checked(textOffset + termNames[term]));
                writer.Write(checked(postingOffset + termStarts[term])); writer.Write(termCounts[term]);
            }
            foreach (var term in lookup) writer.Write(term);
            writer.Write(groups.Count);
            foreach (var group in groups)
            { writer.Write(group.Parent); writer.Write(checked(childData + group.Start * 4)); writer.Write(group.Count); }
            foreach (var child in _children) writer.Write((int)child);
            Copy(_tokens, output); Copy(postings, output); Copy(_metadata, output); Copy(_text, output);
            output.Write(state);
            if (output.Position != payloadLength) throw new InvalidDataException("Packed katalog uzunluğu uyuşmuyor.");
            output.Flush(); output.Position = 0;
            var hash = SHA256.HashData(output);
            output.Position = payloadLength; output.Write(hash); output.Flush(true);
        }
    }

    private FileStream NewFile(string name) => new(Path.Combine(_workspace, name), FileMode.CreateNew,
        FileAccess.ReadWrite, FileShare.None, 128 * 1024, FileOptions.DeleteOnClose);
    private static void Copy(Stream source, Stream output) { source.Position = 0; source.CopyTo(output); }
    public void Dispose()
    {
        _rowWriter.Dispose(); _metaWriter.Dispose();
        _rows.Dispose(); _text.Dispose(); _metadata.Dispose(); _tokens.Dispose();
        _terms.Clear(); _names.Clear(); _postings.Clear(); _children.Clear();
    }
}
