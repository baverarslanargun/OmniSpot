using System.Text;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

internal enum TokenMatch
{
    Exact,
    Partial,
    Fuzzy
}

public sealed partial class FileIndex
{
    private const int NearWordLimit = 64;
    private const int ItemBlock = 1 << 16;
    private readonly object _wordsGate = new();
    private IndexWords? _words;

    internal IReadOnlyList<SearchItem> TokenItems(string token, TokenMatch mode, int maxDistance, IReadOnlyList<string>? scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return [];
        var upper = Encoding.UTF8.GetBytes(token.ToUpperInvariant());
        var plain = Encoding.UTF8.GetBytes(IndexNames.Plain(token));
        _lock.EnterReadLock();
        try
        {
            var rows = ResolveScope(scope);
            var bits = _names.Find(upper, plain, mode != TokenMatch.Partial, cancellationToken);
            var items = ItemsNamed(bits, rows, cancellationToken);
            if (mode != TokenMatch.Fuzzy || items.Count > 0) return items;
            var near = new ulong[bits.Length];
            foreach (var word in NearWords(plain, maxDistance, cancellationToken))
            {
                var found = _names.Find(word, word, true, cancellationToken);
                for (var i = 0; i < near.Length; i++) near[i] |= found[i];
            }
            return ItemsNamed(near, rows, cancellationToken);
        }
        finally { _lock.ExitReadLock(); }
    }

    internal void PrepareWords(CancellationToken cancellationToken)
    {
        while (true)
        {
            _lock.EnterReadLock();
            try
            {
                lock (_wordsGate)
                {
                    if (_words is null || _words.Source != _names) _words = new IndexWords(_names);
                    _words.Extend(1 << 16, cancellationToken);
                    if (!_words.Complete) continue;
                    _words.Trim();
                    return;
                }
            }
            finally { _lock.ExitReadLock(); }
        }
    }

    private List<byte[]> NearWords(byte[] word, int maxDistance, CancellationToken cancellationToken)
    {
        lock (_wordsGate)
        {
            if (_words is null || _words.Source != _names) _words = new IndexWords(_names);
            _words.Extend(int.MaxValue, cancellationToken);
            return _words.Near(word, maxDistance, NearWordLimit, cancellationToken);
        }
    }

    private List<SearchItem> ItemsNamed(ulong[] bits, HashSet<uint>? scope, CancellationToken cancellationToken)
    {
        var result = new List<SearchItem>();
        if (!bits.AsSpan().ContainsAnyExcept(0UL)) return result;
        for (var row = 0u; row < _count; row++)
        {
            if ((row & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            var name = _name[row];
            if ((bits[name >> 6] & 1UL << (name & 63)) == 0 || row == _root || !IsAlive(row) || !InScope(row, scope)) continue;
            result.Add(ToItem(row));
        }
        return result;
    }

    internal IEnumerable<SearchItem> Items(QueryCatalogFilter filter, IReadOnlyList<string>? scope, CancellationToken cancellationToken)
    {
        if (filter.PathSegments is { Count: 0 }) yield break;
        using var names = filter.PathSegments is not null || filter.AllowedExtensions is { Count: > 0 }
            ? new CatalogNameFilter(filter, RootPath)
            : null;
        var next = 0u;
        var generation = -1;
        var block = new List<SearchItem>();
        while (next != NoRow)
        {
            block.Clear();
            (next, generation) = ReadItems(filter, names, scope, next, generation, block, cancellationToken);
            foreach (var item in block) yield return item;
        }
    }

    private (uint Next, int Generation) ReadItems(QueryCatalogFilter filter, CatalogNameFilter? names,
        IReadOnlyList<string>? scope, uint start, int generation, List<SearchItem> block, CancellationToken cancellationToken)
    {
        _lock.EnterReadLock();
        try
        {
            if (generation >= 0 && generation != _generation) return (NoRow, generation);
            names?.Prepare(_names, cancellationToken);
            var rows = ResolveScope(scope);
            var dates = filter.CreatedAfter is not null || filter.CreatedBeforeExclusive is not null ||
                filter.ModifiedAfter is not null || filter.ModifiedBeforeExclusive is not null;
            var end = (uint)Math.Min(_count, (long)start + ItemBlock);
            for (var row = start; row < end; row++)
            {
                if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (row == _root || !IsAlive(row)) continue;
                var directory = IsDirectory(row);
                if (!filter.Matches(directory, directory ? null : _size[row], dates ? CreatedOf(row) : null, dates ? ModifiedOf(row) : null) ||
                    !InScope(row, rows)) continue;
                if (names is not null && !names.Matches(this, row, directory)) continue;
                block.Add(ToItem(row));
            }
            return (end >= _count ? NoRow : end, _generation);
        }
        finally { _lock.ExitReadLock(); }
    }
}
