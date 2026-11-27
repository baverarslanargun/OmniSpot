using System.Runtime.InteropServices;
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

    internal IReadOnlyList<(SearchItem Item, bool Exact)> TokenItemsWithPartial(string token, IReadOnlyList<string>? scope,
        CancellationToken cancellationToken) => TokenItemsWithPartial(token, scope, null, out _, cancellationToken);

    internal IReadOnlyList<(SearchItem Item, bool Exact)> TokenItemsWithPartial(string token, IReadOnlyList<string>? scope,
        QueryCatalogFilter? filter, out bool found, CancellationToken cancellationToken)
    {
        found = false;
        if (string.IsNullOrWhiteSpace(token)) return [];
        var upper = Encoding.UTF8.GetBytes(token.ToUpperInvariant());
        var plain = Encoding.UTF8.GetBytes(IndexNames.Plain(token));
        _lock.EnterReadLock();
        try
        {
            var rows = ResolveScope(scope);
            var result = new List<(SearchItem Item, bool Exact)>();
            var partial = _names.Find(upper, plain, false, cancellationToken);
            if (!partial.AsSpan().ContainsAnyExcept(0UL)) return result;
            var exact = _names.Find(upper, plain, true, cancellationToken);
            var extensions = filter?.AllowedExtensions is { Count: > 0 } allowed ? allowed.ToArray() : null;
            for (var row = 0u; row < _count; row++)
            {
                if ((row & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                var name = _name[row];
                var bit = 1UL << (name & 63);
                if ((partial[name >> 6] & bit) == 0 || row == _root || !IsAlive(row) || !InScope(row, rows)) continue;
                found = true;
                if (filter is { } candidateFilter && !MatchesTokenCandidate(row, candidateFilter, extensions)) continue;
                result.Add((ToItem(row), (exact[name >> 6] & bit) != 0));
            }
            return result;
        }
        finally { _lock.ExitReadLock(); }
    }

    private bool MatchesTokenCandidate(uint row, QueryCatalogFilter filter, string[]? extensions)
    {
        var directory = IsDirectory(row);
        var created = filter.CreatedAfter != null || filter.CreatedBeforeExclusive != null ? CreatedOf(row) : null;
        DateTime? modified = filter.ModifiedAfter != null || filter.ModifiedBeforeExclusive != null ? ModifiedOf(row) : null;
        if (!filter.Matches(directory, directory ? null : _size[row], created, modified)) return false;
        if (extensions == null || directory && filter.IncludeDirectoriesWithoutExtension) return true;

        var name = _name[row];
        var bytes = _names.Original(name);
        if (_names.IsWide(name))
        {
            var extension = Path.GetExtension(MemoryMarshal.Cast<byte, char>(bytes));
            foreach (var allowed in extensions)
                if (extension.Equals(allowed.AsSpan(), StringComparison.OrdinalIgnoreCase)) return true;
            return false;
        }

        var dot = bytes.LastIndexOf((byte)'.');
        if (dot < 0 || dot == bytes.Length - 1) return false;
        var suffix = bytes[dot..];
        if (!Ascii.IsValid(suffix)) return filter.AllowedExtensions!.Contains(Encoding.UTF8.GetString(suffix));
        foreach (var allowed in extensions)
            if (Ascii.EqualsIgnoreCase(suffix, allowed.AsSpan())) return true;
        return false;
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

    private List<byte[]> NearWords(byte[] word, int maxDistance, CancellationToken cancellationToken, int limit = NearWordLimit)
    {
        lock (_wordsGate)
        {
            if (_words is null || _words.Source != _names) _words = new IndexWords(_names);
            _words.Extend(int.MaxValue, cancellationToken);
            return _words.Near(word, maxDistance, limit, cancellationToken);
        }
    }

    internal TokenSummary SummarizeToken(string token, int sampleCount, IReadOnlyList<string>? scope,
        CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(token)) return new TokenSummary(0, []);
        var upper = Encoding.UTF8.GetBytes(token.ToUpperInvariant());
        var plain = Encoding.UTF8.GetBytes(IndexNames.Plain(token));
        _lock.EnterReadLock();
        try
        {
            var rows = ResolveScope(scope);
            var bits = _names.Find(upper, plain, true, cancellationToken);
            var samples = new List<string>(Math.Max(0, sampleCount));
            if (!bits.AsSpan().ContainsAnyExcept(0UL)) return new TokenSummary(0, samples);
            var count = 0;
            for (var row = 0u; row < _count; row++)
            {
                if ((row & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (!IsNamedRow(bits, row, rows)) continue;
                if (samples.Count < sampleCount) samples.Add(ToItem(row).Name);
                count++;
            }
            return new TokenSummary(count, samples);
        }
        finally { _lock.ExitReadLock(); }
    }

    private List<SearchItem> ItemsNamed(ulong[] bits, HashSet<uint>? scope, CancellationToken cancellationToken)
    {
        var result = new List<SearchItem>();
        if (!bits.AsSpan().ContainsAnyExcept(0UL)) return result;
        for (var row = 0u; row < _count; row++)
        {
            if ((row & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (IsNamedRow(bits, row, scope)) result.Add(ToItem(row));
        }
        return result;
    }

    private bool IsNamedRow(ulong[] bits, uint row, HashSet<uint>? scope)
    {
        var name = _name[row];
        return (bits[name >> 6] & 1UL << (name & 63)) != 0 && row != _root && IsAlive(row) && InScope(row, scope);
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
            var hasCreatedAfter = filter.CreatedAfter is not null;
            var hasCreatedBefore = filter.CreatedBeforeExclusive is not null;
            var hasModifiedAfter = filter.ModifiedAfter is not null;
            var hasModifiedBefore = filter.ModifiedBeforeExclusive is not null;
            var createdAfter = new LocalTimeBound(filter.CreatedAfter ?? default);
            var createdBefore = new LocalTimeBound(filter.CreatedBeforeExclusive ?? default);
            var modifiedAfter = new LocalTimeBound(filter.ModifiedAfter ?? default);
            var modifiedBefore = new LocalTimeBound(filter.ModifiedBeforeExclusive ?? default);
            var dates = hasCreatedAfter || hasCreatedBefore || hasModifiedAfter || hasModifiedBefore;
            var undated = filter with { CreatedAfter = null, CreatedBeforeExclusive = null, ModifiedAfter = null, ModifiedBeforeExclusive = null };
            var end = (uint)Math.Min(_count, (long)start + ItemBlock);
            for (var row = start; row < end; row++)
            {
                if ((row & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (row == _root || !IsAlive(row)) continue;
                var directory = IsDirectory(row);
                if (dates && (!directory || filter.FilterDirectoryDates))
                {
                    if (hasCreatedAfter || hasCreatedBefore)
                    {
                        var created = _created[row];
                        if (created == 0 || hasCreatedAfter && createdAfter.IsBefore(created) ||
                            hasCreatedBefore && !createdBefore.IsBefore(created)) continue;
                    }
                    var modified = _modified[row];
                    if (hasModifiedAfter && modifiedAfter.IsBefore(modified) ||
                        hasModifiedBefore && !modifiedBefore.IsBefore(modified)) continue;
                }
                if (!undated.Matches(directory, directory ? null : _size[row], null, null) || !InScope(row, rows)) continue;
                if (names is not null && !names.Matches(this, row, directory)) continue;
                block.Add(ToItem(row));
            }
            return (end >= _count ? NoRow : end, _generation);
        }
        finally { _lock.ExitReadLock(); }
    }

    private readonly struct LocalTimeBound
    {
        private static readonly long Margin = TimeSpan.FromHours(14).Ticks;
        private readonly DateTime _bound;
        private readonly long _surelyBefore;
        private readonly long _surelyNotBefore;

        internal LocalTimeBound(DateTime bound)
        {
            _bound = bound;
            _surelyBefore = WholeSeconds(bound.Ticks - Margin);
            _surelyNotBefore = WholeSeconds(bound.Ticks + Margin);
        }

        private static long WholeSeconds(long ticks)
        {
            var quotient = Math.DivRem(ticks - DateTime.UnixEpoch.Ticks, TimeSpan.TicksPerSecond, out var remainder);
            return remainder > 0 ? quotient + 1 : quotient;
        }

        internal bool IsBefore(uint seconds) =>
            seconds < _surelyBefore ||
            seconds < _surelyNotBefore && DateTime.UnixEpoch.AddSeconds(seconds).ToLocalTime() < _bound;
    }
}
