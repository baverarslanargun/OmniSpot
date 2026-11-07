using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Search.Paged;

[StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Posting { internal int Count; internal uint First; internal int Head, Tail; }
[StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Word { internal int Offset; internal ushort Length, Flags; internal Posting Post; }
internal sealed partial class Catalog
{
    internal IEnumerable<string> TokenNames()
    {
        for(var i=1;i<Data.Count(Image.Words);i++) yield return WordText(i);
    }
    readonly BasicTokenizer _tokenizer = new();
    static uint WordHash(ReadOnlySpan<char> value)
    { uint hash = 2166136261; while (!value.IsEmpty) { int used; uint v; if (Rune.DecodeFromUtf16(value, out var rune, out used) == System.Buffers.OperationStatus.Done) v = (uint)Rune.ToUpperInvariant(rune).Value; else { v = (uint)(0x110000 + value[0]); used = 1; } hash = unchecked((hash ^ v) * 16777619); value = value[used..]; } return hash; }
    string WordText(int id) { var w = Data.Read<Word>(Image.Words, id); return Encoding.UTF8.GetString(Data.Bytes(Image.WordText, w.Offset, w.Length)); }
    bool WordEquals(int id, ReadOnlySpan<char> token)
    { var w = Data.Read<Word>(Image.Words, id); var bytes = Data.Bytes(Image.WordText, w.Offset, w.Length); Span<char> chars = stackalloc char[4096]; var length = Encoding.UTF8.GetChars(bytes, chars); return MemoryExtensions.Equals(chars[..length], token, StringComparison.OrdinalIgnoreCase); }
    int FindWord(ReadOnlySpan<char> token)
    { var slot = (int)WordHash(token) & (Data.WordHashSize - 1); while (true) { var word = Data.Read<int>(Image.WordHash, slot); if (word == 0 || WordEquals(word, token)) return word; slot = (slot + 1) & (Data.WordHashSize - 1); } }
    void GrowWords()
    { Data.WordHashSize *= 2; Data.ZeroRows(Image.WordHash, Data.WordHashSize); for (int id = 1; id < Data.Count(Image.Words); id++) { var slot = (int)WordHash(WordText(id)) & (Data.WordHashSize - 1); while (Data.Read<int>(Image.WordHash, slot) != 0) slot = (slot + 1) & (Data.WordHashSize - 1); Data.Write(Image.WordHash, slot, id); } }
    int InternWord(ReadOnlySpan<char> token)
    {
        var existing = FindWord(token); if (existing != 0) return existing; if ((Data.Count(Image.Words) + 1) * 10 >= Data.WordHashSize * 7L) GrowWords();
        Span<byte> bytes = stackalloc byte[4096]; var length = Encoding.UTF8.GetBytes(token, bytes); var ascii = true; foreach (var c in token) if (c > 127) { ascii = false; break; }
        var word = new Word { Offset = Data.Append(Image.WordText, bytes[..length]), Length = checked((ushort)length), Flags = (ushort)(ascii ? 1 : 0) }; var id = Data.Add(Image.Words, word);
        var slot = (int)WordHash(token) & (Data.WordHashSize - 1); while (Data.Read<int>(Image.WordHash, slot) != 0) slot = (slot + 1) & (Data.WordHashSize - 1); Data.Write(Image.WordHash, slot, id); AddGrams(id, bytes[..length], ascii); return id;
    }
    void AddGrams(int word, ReadOnlySpan<byte> text, bool ascii)
    {
        void Add(int key) { var list = Data.Read<Posting>(Image.Grams, key); AddPosting(ref list, (uint)word); Data.Write(Image.Grams, key, list); }
        if (!ascii) { Add(0); return; }
        Span<ulong> seen = stackalloc ulong[256]; seen.Clear(); for (int i = 1; i < text.Length; i++) { var key = text[i - 1] * 128 + text[i]; var bit = 1UL << (key & 63); if ((seen[key >> 6] & bit) != 0) continue; seen[key >> 6] |= bit; Add(key + 1); }
    }
    void AddTerms(uint id, string name)
    {
        if (!TokenIndexing.Enabled) return;
        Span<char> scratch = stackalloc char[1024]; using var parts = new BasicTokenEnumerator(name, scratch, _tokenizer);
        while (parts.MoveNext()) { var key = InternWord(parts.Current); var word = Data.Read<Word>(Image.Words, key); AddPosting(ref word.Post, id); Data.Write(Image.Words, key, word); }
    }
    void AddPosting(ref Posting list, uint id)
    {
        if (list.Count == 0) { list.First = id; list.Count = 1; return; }
        if (list.Count == 1) { if (list.First == id) return; list.Head = (int)id; list.Count = 2; return; }
        if (list.Count == 2) { if ((uint)list.Head == id) return; list.Tail = (int)id; list.Count = 3; return; }
        if (list.Count == 3) { if ((uint)list.Tail == id) return; var second = (uint)list.Head; var third = (uint)list.Tail; list.Head = list.Tail = 0; AddDelta(ref list, list.First, second); AddDelta(ref list, second, third); AddDelta(ref list, third, id); list.Count = 4; return; }
        var last = BinaryPrimitives.ReadUInt32LittleEndian(Data.Bytes(Image.Postings, list.Tail + 5, 4)); if (last == id) return; AddDelta(ref list, last, id); list.Count++;
    }
    void AddDelta(ref Posting list, uint previous, uint id)
    {
        var difference = (long)id - previous; Span<byte> bytes = stackalloc byte[10]; var count = Var(Zig(difference), bytes); var used = list.Tail == 0 ? 32 : Data.Bytes(Image.Postings, list.Tail + 4, 1)[0];
        if (used + count > 23) { var block = Data.Append(Image.Postings, new byte[32]); if (list.Tail == 0) list.Head = block; else BinaryPrimitives.WriteInt32LittleEndian(Data.MutableBytes(Image.Postings, list.Tail, 4), block); list.Tail = block; used = 0; }
        bytes[..count].CopyTo(Data.MutableBytes(Image.Postings, list.Tail + 9 + used, count)); Data.MutableBytes(Image.Postings, list.Tail + 4, 1)[0] = (byte)(used + count); BinaryPrimitives.WriteUInt32LittleEndian(Data.MutableBytes(Image.Postings, list.Tail + 5, 4), id);
    }
    void Visit(Posting list, Action<uint> action)
    {
        if (list.Count == 0) return; var id = list.First; action(id); if (list.Count <= 3) { if (list.Count >= 2) action((uint)list.Head); if (list.Count == 3) action((uint)list.Tail); return; }
        var visited = 1;
        for (var block = list.Head; block != 0; block = BinaryPrimitives.ReadInt32LittleEndian(Data.Bytes(Image.Postings, block, 4)))
        { var used = Data.Bytes(Image.Postings, block + 4, 1)[0]; if (used > 23) throw new InvalidDataException("Posting length"); var bytes = Data.Bytes(Image.Postings, block + 9, used); var at = 0; while (at < bytes.Length) { id = checked((uint)(id + Unzig(Unvar(bytes, ref at)))); action(id); if (++visited > list.Count) throw new InvalidDataException("Posting cycle"); } }
        if (visited != list.Count) throw new InvalidDataException("Posting count");
    }
    bool TermVisible(uint id, string token)
    {
        if (!Alive(id)) return false; if ((Row(id).Name & Dirty) == 0) return true;
        var name = Name(id); Span<char> scratch = stackalloc char[1024]; using var parts = new BasicTokenEnumerator(name, scratch, _tokenizer); while (parts.MoveNext()) if (parts.Current.Equals(token.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true; return false;
    }
    internal void FindMatches(string token, int mode, Action<uint> output, int maxDistance = 2, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested(); int visited = 0;
        void Check() { if ((++visited & 4095) == 0) cancellationToken.ThrowIfCancellationRequested(); }
        bool any = false; void Collect(int key) { var word = Data.Read<Word>(Image.Words, key); string? original = null; var entries = new PostingCursor(Data, word.Post); while (entries.MoveNext()) { Check(); var id = entries.Current; if (!Exists(id)) throw new InvalidDataException("Posting identity"); var row = Row(id); if (row.Name <= 0) continue; if ((row.Name & Dirty) != 0 && !TermVisible(id, original ??= WordText(key))) continue; any = true; output(id); } }
        if (mode == 0 || mode == 2) { var exact = FindWord(token); if (exact != 0) Collect(exact); if (mode == 0 || any) return; }
        var ascii = token.All(c => c <= 127); var query = Encoding.UTF8.GetBytes(token.ToLowerInvariant()); Span<char> decoded = stackalloc char[4096];
        if (mode == 1 && ascii && query.Length >= 2)
        {
            var entries = new PostingCursor(Data, Data.Read<Posting>(Image.Grams, query[0] * 128 + query[1] + 1)); while (entries.MoveNext()) { var key = (int)entries.Current; var word = Data.Read<Word>(Image.Words, key); if (Data.Bytes(Image.WordText, word.Offset, word.Length).IndexOf(query) >= 0) Collect(key); }
            entries = new PostingCursor(Data, Data.Read<Posting>(Image.Grams, 0)); while (entries.MoveNext()) { var key = (int)entries.Current; var word = Data.Read<Word>(Image.Words, key); var n = Encoding.UTF8.GetChars(Data.Bytes(Image.WordText, word.Offset, word.Length), decoded); if (MemoryExtensions.Contains(decoded[..n], token.AsSpan(), StringComparison.OrdinalIgnoreCase)) Collect(key); }
            return;
        }
        for (int key = 1; key < Data.Count(Image.Words); key++)
        {
            Check(); var word = Data.Read<Word>(Image.Words, key); var bytes = Data.Bytes(Image.WordText, word.Offset, word.Length); if (mode == 1 && ascii && word.Flags == 1) { if (bytes.IndexOf(query) >= 0) Collect(key); continue; }
            if (mode == 2 && word.Flags == 1 && Math.Abs(bytes.Length - token.Length) > maxDistance) continue; var n = Encoding.UTF8.GetChars(bytes, decoded); if (mode == 1 ? MemoryExtensions.Contains(decoded[..n], token.AsSpan(), StringComparison.OrdinalIgnoreCase) : Distance(decoded[..n], token, maxDistance)) Collect(key);
        }
    }
    ref struct PostingCursor
    {
        readonly Image _data; readonly Posting _list; int _count, _next, _at; ReadOnlySpan<byte> _bytes;
        internal uint Current { get; private set; }
        internal PostingCursor(Image data, Posting list) { _data = data; _list = list; _count = 0; _next = list.Head; _at = 0; _bytes = []; Current = 0; }
        internal bool MoveNext()
        {
            if (_count == _list.Count) return false;
            if (_count++ == 0) { Current = _list.First; return true; }
            if (_list.Count <= 3) { Current = (uint)(_count == 2 ? _list.Head : _list.Tail); return true; }
            if (_at == _bytes.Length) { if (_next == 0) throw new InvalidDataException("Posting truncated"); var block = _data.Bytes(Image.Postings, _next, 32); _next = BinaryPrimitives.ReadInt32LittleEndian(block); var used = block[4]; if (used == 0 || used > 23) throw new InvalidDataException("Posting length"); _bytes = block.Slice(9, used); _at = 0; }
            Current = checked((uint)(Current + Unzig(Unvar(_bytes, ref _at)))); return true;
        }
    }
    static bool Distance(ReadOnlySpan<char> value, ReadOnlySpan<char> query, int max)
    {
        if (Math.Abs(value.Length - query.Length) > max) return false; Span<int> a = query.Length < 1024 ? stackalloc int[query.Length + 1] : new int[query.Length + 1]; Span<int> b = query.Length < 1024 ? stackalloc int[query.Length + 1] : new int[query.Length + 1]; for (int j = 0; j <= query.Length; j++) a[j] = j;
        for (int i = 1; i <= value.Length; i++) { b[0] = i; var best = i; for (int j = 1; j <= query.Length; j++) { b[j] = Math.Min(Math.Min(a[j] + 1, b[j - 1] + 1), a[j - 1] + (char.ToUpperInvariant(value[i - 1]) == char.ToUpperInvariant(query[j - 1]) ? 0 : 1)); best = Math.Min(best, b[j]); } if (best > max) return false; var t = a; a = b; b = t; }
        return a[query.Length] <= max;
    }
    internal List<uint> QueryIds(string token, int mode, int maxDistance = 2, CancellationToken cancellationToken = default) { using var bits = new Bits(MaxId); FindMatches(token, mode, bits.Add, maxDistance, cancellationToken); return bits.Values().ToList(); }
    internal bool HasToken(uint id, string token, StringComparison comparison)
    { var name = Name(id); Span<char> scratch = stackalloc char[1024]; using var parts = new BasicTokenEnumerator(name, scratch, _tokenizer); while (parts.MoveNext()) if (parts.Current.Equals(token.AsSpan(), comparison)) return true; return false; }
}
