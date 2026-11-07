using System.Text;

namespace SmartFileLauncher.Core.Index;

internal sealed class IndexWords(IndexNames source)
{
    private static readonly char[] Separators = [' ', '_', '-', '.', ',', '[', ']', '(', ')'];
    private byte[] _pool = new byte[1 << 16];
    private int _used;
    private int[] _offset = new int[1024];
    private int[] _next = new int[1024];
    private int[] _buckets = new int[1024];
    private int _count;
    private int _built;

    internal IndexNames Source => source;

    internal bool Complete => _built >= source.Count;

    internal void Extend(int limit, CancellationToken cancellationToken)
    {
        var end = (int)Math.Min(source.Count, (long)_built + limit);
        for (var id = _built; id < end; id++)
        {
            if ((id & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (source.IsAscii(id)) AddParts(source.Folded(id));
            else
                foreach (var part in source.Text(id).Split(Separators, StringSplitOptions.RemoveEmptyEntries))
                    Add(Encoding.UTF8.GetBytes(IndexNames.Plain(part)));
            _built = id + 1;
        }
    }

    private void AddParts(ReadOnlySpan<byte> name)
    {
        var start = 0;
        for (var at = 0; at <= name.Length; at++)
        {
            if (at < name.Length && !IndexNames.IsWordBreak(name[at])) continue;
            if (at > start) Add(name[start..at]);
            start = at + 1;
        }
    }

    internal void Trim()
    {
        Array.Resize(ref _pool, _used + Math.Max(1 << 12, _used / 64));
        var size = _count + Math.Max(256, _count / 64);
        Array.Resize(ref _offset, size);
        Array.Resize(ref _next, size);
    }

    private void Add(ReadOnlySpan<byte> word)
    {
        if (word.Length < 3 || word.Length > 64 || word.IndexOf((byte)0) >= 0 || word.ContainsAnyInRange((byte)'0', (byte)'9')) return;
        var hash = Hash(word);
        for (var id = _buckets[hash & (_buckets.Length - 1)] - 1; id >= 0; id = _next[id] - 1)
            if (Word(id).SequenceEqual(word)) return;
        if (_count == _offset.Length)
        {
            Array.Resize(ref _offset, _count + _count / 2);
            Array.Resize(ref _next, _offset.Length);
        }
        if (_used + word.Length + 1 > _pool.Length)
            Array.Resize(ref _pool, Math.Max(_used + word.Length + 1, _pool.Length + _pool.Length / 2));
        word.CopyTo(_pool.AsSpan(_used));
        _offset[_count] = _used;
        _used += word.Length;
        _pool[_used++] = 0;
        _count++;
        if (_count > _buckets.Length) Rehash(_buckets.Length * 2);
        else Link(_count - 1, hash);
    }

    private ReadOnlySpan<byte> Word(int id)
    {
        var end = id + 1 < _count ? _offset[id + 1] : _used;
        return _pool.AsSpan(_offset[id], end - _offset[id] - 1);
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
        for (var id = 0; id < _count; id++) Link(id, Hash(Word(id)));
    }

    private static uint Hash(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037UL;
        foreach (var value in bytes)
        {
            hash ^= value;
            hash *= 1099511628211UL;
        }
        return (uint)(hash ^ hash >> 32);
    }

    internal List<byte[]> Near(byte[] word, int maxDistance, int limit, CancellationToken cancellationToken)
    {
        var found = new List<(int Distance, int Id)>();
        if (word.Length == 0 || word.Length > 256) return [];
        for (var id = 0; id < _count; id++)
        {
            if ((id & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            var candidate = Word(id);
            if (Math.Abs(candidate.Length - word.Length) > maxDistance) continue;
            var distance = Distance(candidate, word, maxDistance);
            if (distance <= maxDistance) found.Add((distance, id));
        }
        return found.OrderBy(match => match.Distance).ThenBy(match => match.Id).Take(limit)
            .Select(match => Word(match.Id).ToArray()).ToList();
    }

    internal static int Distance(ReadOnlySpan<byte> left, ReadOnlySpan<byte> right, int limit)
    {
        Span<int> previous = stackalloc int[right.Length + 1];
        Span<int> current = stackalloc int[right.Length + 1];
        for (var j = 0; j <= right.Length; j++) previous[j] = j;
        for (var i = 1; i <= left.Length; i++)
        {
            current[0] = i;
            var best = i;
            for (var j = 1; j <= right.Length; j++)
            {
                var cost = left[i - 1] == right[j - 1] ? 0 : 1;
                current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + cost);
                best = Math.Min(best, current[j]);
            }
            if (best > limit) return limit + 1;
            var swap = previous;
            previous = current;
            current = swap;
        }
        return previous[right.Length];
    }
}
