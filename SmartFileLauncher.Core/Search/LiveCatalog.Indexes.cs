using System.Buffers;
using System.Text;

namespace SmartFileLauncher.Core.Search;

internal sealed partial class LiveCatalog
{
    private static uint Hash(int parent, ReadOnlySpan<char> name)
    {
        var hash = unchecked(2166136261u ^ (uint)parent * 16777619u);
        while (!name.IsEmpty)
        {
            uint value; int used;
            var ch = name[0];
            if (ch <= 127) { value = (uint)(ch is >= 'a' and <= 'z' ? ch - 32 : ch); used = 1; }
            else if (Rune.DecodeFromUtf16(name, out var rune, out used) == OperationStatus.Done) value = (uint)Rune.ToUpperInvariant(rune).Value;
            else { value = (uint)(0x110000 + ch); used = 1; }
            hash = unchecked((hash ^ value) * 16777619u); name = name[used..];
        }
        return hash;
    }
    private static int Bucket(uint hash, int basis, int split)
    { var bucket = (int)(hash & (uint)(basis - 1)); return bucket < split ? (int)(hash & (uint)(basis * 2 - 1)) : bucket; }
    private bool NameEquals(int offset, ReadOnlySpan<char> name, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        if (_names.TextLength(offset) != name.Length) return false;
        var buffer = ArrayPool<char>.Shared.Rent(Math.Max(name.Length, 1));
        try
        {
            var length = _names.DecodeText(offset, buffer);
            return MemoryExtensions.Equals(buffer.AsSpan(0, length), name, comparison);
        }
        finally { ArrayPool<char>.Shared.Return(buffer); }
    }
    private uint StoredNameHash(int parent, int offset)
    {
        var length = _names.TextLength(offset);
        var buffer = ArrayPool<char>.Shared.Rent(Math.Max(length, 1));
        try { _names.DecodeText(offset, buffer); return Hash(parent, buffer.AsSpan(0, length)); }
        finally { ArrayPool<char>.Shared.Return(buffer); }
    }
    private int FindNode(int parent, ReadOnlySpan<char> name)
    {
        var bucket = Bucket(Hash(parent, name), _nodeBase, _nodeSplit);
        for (var id = _nodeBuckets.Int32(bucket * 4); id != 0; id = _nodes.Int32(Row(id) + 16))
            if (Parent(id) == parent && NameEquals(_nodes.Int32(Row(id) + 4), name)) return id;
        return 0;
    }
    private void InsertNodeHash(int id, uint hash)
    {
        var bucket = Bucket(hash, _nodeBase, _nodeSplit);
        _nodes.PutInt32(Row(id) + 16, _nodeBuckets.Int32(bucket * 4)); _nodeBuckets.PutInt32(bucket * 4, id);
        if (_nodeCount > (_nodeBase + _nodeSplit) * 4) SplitNodes();
    }
    private void SplitNodes()
    {
        var next = _nodeBase + _nodeSplit; _nodeBuckets.Allocate(4);
        var id = _nodeBuckets.Int32(_nodeSplit * 4); _nodeBuckets.PutInt32(_nodeSplit * 4, 0);
        while (id != 0)
        {
            var row = Row(id); var oldNext = _nodes.Int32(row + 16);
            var bucket = (int)(StoredNameHash(Parent(id), _nodes.Int32(row + 4)) & (uint)(_nodeBase * 2 - 1));
            if (bucket != _nodeSplit && bucket != next) throw new InvalidDataException("Live node hash zinciri bozuk.");
            _nodes.PutInt32(row + 16, _nodeBuckets.Int32(bucket * 4)); _nodeBuckets.PutInt32(bucket * 4, id); id = oldNext;
        }
        if (++_nodeSplit == _nodeBase) { _nodeBase *= 2; _nodeSplit = 0; }
    }
    private int FindTerm(ReadOnlySpan<char> token)
    {
        var bucket = Bucket(Hash(0, token), _termBase, _termSplit);
        for (var id = _termBuckets.Int32(bucket * 4); id != 0; id = _terms.Int32(Term(id) + 4))
            if (NameEquals(_terms.Int32(Term(id)), token)) return id;
        return 0;
    }
    private int GetOrAddTerm(ReadOnlySpan<char> token)
    {
        var id = FindTerm(token);
        if (id != 0) return id;
        id = ++_termCount; var row = _terms.Allocate(TermSize);
        _terms.PutInt32(row, AddName(token));
        var bucket = Bucket(Hash(0, token), _termBase, _termSplit);
        _terms.PutInt32(row + 4, _termBuckets.Int32(bucket * 4)); _termBuckets.PutInt32(bucket * 4, id);
        _maxTerm = Math.Max(_maxTerm, token.Length);
        if (_termCount > (_termBase + _termSplit) * 4) SplitTerms();
        return id;
    }
    private void SplitTerms()
    {
        var next = _termBase + _termSplit; _termBuckets.Allocate(4);
        var id = _termBuckets.Int32(_termSplit * 4); _termBuckets.PutInt32(_termSplit * 4, 0);
        while (id != 0)
        {
            var row = Term(id); var oldNext = _terms.Int32(row + 4); var offset = _terms.Int32(row);
            var bucket = (int)(StoredNameHash(0, offset) & (uint)(_termBase * 2 - 1));
            if (bucket != _termSplit && bucket != next) throw new InvalidDataException("Live term hash zinciri bozuk.");
            _terms.PutInt32(row + 4, _termBuckets.Int32(bucket * 4)); _termBuckets.PutInt32(bucket * 4, id); id = oldNext;
        }
        if (++_termSplit == _termBase) { _termBase *= 2; _termSplit = 0; }
    }
    private void AddPosting(int term, int item)
    {
        var row = Term(term); var count = _terms.Int32(row + 8);
        if (count != 0 && (count <= 3 ? _terms.Int32(row + 8 + count * 4) : _postings.Int32(_terms.Int32(row + 20) + 5)) == item) return;
        if (count < 3)
        { _terms.PutInt32(row + 12 + count * 4, item); _terms.PutInt32(row + 8, count + 1); return; }
        if (count == 3)
        {
            var first = _terms.Int32(row + 12); var second = _terms.Int32(row + 16); var third = _terms.Int32(row + 20);
            _terms.PutInt32(row + 16, 0); _terms.PutInt32(row + 20, 0);
            AppendPostingDelta(row, first, second); AppendPostingDelta(row, second, third); AppendPostingDelta(row, third, item);
        }
        else AppendPostingDelta(row, _postings.Int32(_terms.Int32(row + 20) + 5), item);
        _terms.PutInt32(row + 8, checked(count + 1));
    }
    private void AppendPostingDelta(int row, int previous, int item)
    {
        var delta = (long)item - previous;
        var unsigned = unchecked((ulong)((delta << 1) ^ (delta >> 63)));
        Span<byte> bytes = stackalloc byte[10]; var size = 0;
        do { bytes[size++] = (byte)((unsigned & 127) | (unsigned >= 128 ? 128u : 0)); unsigned >>= 7; } while (unsigned != 0);
        var tail = _terms.Int32(row + 20);
        var used = tail == 0 ? PostingBlock : _postings.At(tail + 4);
        if (used + size > PostingBlock - 9)
        {
            var block = _postings.Allocate(PostingBlock);
            if (tail == 0) _terms.PutInt32(row + 16, block); else _postings.PutInt32(tail, block);
            _terms.PutInt32(row + 20, block); tail = block; used = 0;
        }
        _postings.Put(tail + 9 + used, bytes[..size]);
        Span<byte> length = stackalloc byte[1]; length[0] = checked((byte)(used + size)); _postings.Put(tail + 4, length);
        _postings.PutInt32(tail + 5, item);
    }
    private IEnumerable<int> Postings(int term, LiveReadStamp stamp)
    {
        if (term == 0 || term > stamp.Terms) yield break;
        var row = Term(term); var count = _terms.Int32(row + 8); var id = _terms.Int32(row + 12);
        if (count == 0) yield break;
        if (Arrived(id, stamp)) yield return id;
        if (count <= 3)
        {
            for (var i = 1; i < count; i++) { id = _terms.Int32(row + 12 + i * 4); if (Arrived(id, stamp)) yield return id; }
            yield break;
        }
        var found = 1;
        for (var block = _terms.Int32(row + 16); block != 0; block = _postings.Int32(block))
        {
            var offset = block + 9; var end = offset + _postings.At(block + 4);
            if (end > block + PostingBlock) throw new InvalidDataException("Live posting sınırı bozuk.");
            while (offset < end)
            {
                id = checked(id + (int)PackedFormat.DecodeSigned(_postings.Unsigned(ref offset, end)));
                if (++found > count) throw new InvalidDataException("Live posting sayısı bozuk.");
                if (Arrived(id, stamp)) yield return id;
            }
        }
        if (found != count) throw new InvalidDataException("Live posting zinciri eksik.");
    }
}
