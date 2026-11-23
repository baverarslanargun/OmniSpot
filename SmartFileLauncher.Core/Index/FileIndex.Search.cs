using System.Numerics;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Index;

public sealed partial class FileIndex
{
    internal int SparseNameLimit { get; set; } = 4096;

    internal int SparseRowLimit { get; set; } = 16384;

    public IReadOnlyList<SearchResult> Search(string query, int limit, ResultView view, CancellationToken cancellationToken = default) =>
        Search(query, limit, view, null, cancellationToken);

    public IReadOnlyList<SearchResult> Search(string query, int limit, ResultView view, IReadOnlyList<string>? scope,
        CancellationToken cancellationToken = default) =>
        Search(query, 0, limit, view, scope, cancellationToken);

    public IReadOnlyList<SearchResult> Search(string query, int offset, int limit, ResultView view, IReadOnlyList<string>? scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        ArgumentOutOfRangeException.ThrowIfNegative(offset);
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return [];
        var pattern = IndexNames.FoldQuery(query);
        _lock.EnterReadLock();
        try
        {
            var window = (int)Math.Min(int.MaxValue, (long)offset + limit);
            if (view.Sort.IsRelevance && view.LiteralRanking is { } ranking)
                return SearchRankedLiteral(query, pattern, offset, window, view.Filter, ResolveScope(scope, view.Scope), ranking, cancellationToken);

            var matches = _names.Match(pattern, cancellationToken);
            var rows = Collect(matches, window, view, ResolveScope(scope, view.Scope), cancellationToken);
            if (rows.Count <= offset) return [];
            var results = new SearchResult[rows.Count - offset];
            for (var i = 0; i < results.Length; i++) results[i] = ToResult(rows[offset + i]);
            return results;
        }
        finally { _lock.ExitReadLock(); }
    }

    public int CountMatches(string query, ResultView view, IReadOnlyList<string>? scope, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (string.IsNullOrWhiteSpace(query)) return 0;
        var pattern = IndexNames.FoldQuery(query);
        _lock.EnterReadLock();
        try
        {
            var matches = _names.Match(pattern, cancellationToken);
            var scopeRows = ResolveScope(scope, view.Scope);
            var order = SparseOrder(matches, cancellationToken) ?? _byName;
            var filter = view.Filter;
            var needsName = filter.Categories != FileFilterCategory.None;
            var now = DateTime.Now;
            var count = 0;
            for (var i = 0; i < order.Count; i++)
            {
                if ((i & 4095) == 4095) cancellationToken.ThrowIfCancellationRequested();
                if (Accepts(order.Rows[i], matches, filter, needsName, scopeRows, now)) count++;
            }
            return count;
        }
        finally { _lock.ExitReadLock(); }
    }

    private bool Accepts(uint row, ulong[] matches, ItemFilter filter, bool needsName, HashSet<uint>? scope, DateTime now)
    {
        var name = _name[row];
        return IsMatchedName(matches, name) && AcceptsMatchedRow(row, filter, needsName, scope, now);
    }

    private static bool IsMatchedName(ReadOnlySpan<ulong> matches, int name) => (matches[name >> 6] & 1UL << (name & 63)) != 0;

    private bool AcceptsMatchedRow(uint row, ItemFilter filter, bool needsName, HashSet<uint>? scope, DateTime now)
    {
        if (row == _root || !InScope(row, scope)) return false;
        if (!filter.IsActive) return true;
        var directory = IsDirectory(row);
        return filter.Matches(needsName ? _names.Text(_name[row]) : "", directory, directory ? null : _size[row],
            directory ? null : ModifiedOf(row), now);
    }

    private List<uint> Collect(ulong[] matches, int limit, ResultView view, HashSet<uint>? scope, CancellationToken cancellationToken)
    {
        var selected = new List<uint>(Math.Min(limit, 1024));
        var filter = view.Filter;
        var needsName = filter.Categories != FileFilterCategory.None;
        var now = DateTime.Now;
        var visited = 0;

        bool Take(uint row)
        {
            if ((++visited & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (!Accepts(row, matches, filter, needsName, scope, now)) return false;
            selected.Add(row);
            return selected.Count == limit;
        }

        bool Forward(RowOrder order, bool directoriesOnly = false)
        {
            for (var i = 0; i < order.Count; i++)
            {
                var row = order.Rows[i];
                if (directoriesOnly && !IsDirectory(row)) continue;
                if (Take(row)) return true;
            }
            return false;
        }

        bool Backward(RowOrder order, Func<uint, uint, bool> sameKey)
        {
            for (var end = order.Count - 1; end >= 0;)
            {
                var start = end;
                while (start > 0 && sameKey(order.Rows[start - 1], order.Rows[end])) start--;
                for (var i = start; i <= end; i++)
                    if (Take(order.Rows[i])) return true;
                end = start - 1;
            }
            return false;
        }

        var sort = view.Sort;
        var sparse = SparseOrder(matches, cancellationToken);
        var byName = sparse ?? _byName;
        switch (sort.Field)
        {
            case SortField.Size:
            case SortField.Modified:
            {
                var order = sparse is null ? EnsureOrder(sort.Field, cancellationToken) : SortedSubset(sparse, sort.Field);
                var done = sort.Descending
                    ? Backward(order, sort.Field == SortField.Size
                        ? (left, right) => _size[left] == _size[right]
                        : (left, right) => _modified[left] == _modified[right])
                    : Forward(order);
                if (!done) Forward(byName, directoriesOnly: true);
                break;
            }
            case SortField.Kind:
            {
                var order = sparse is null ? EnsureOrder(SortField.Kind, cancellationToken) : SortedSubset(sparse, SortField.Kind);
                if (sort.Descending) Backward(order, (left, right) => IndexNames.CompareIgnoreCase(KindOf(left), KindOf(right)) == 0);
                else Forward(order);
                break;
            }
            case SortField.Name when sort.Descending:
                Backward(byName, (left, right) => _name[left] == _name[right]);
                break;
            default:
                Forward(byName);
                break;
        }
        return selected;
    }

    private RowOrder? SparseOrder(ReadOnlySpan<ulong> matches, CancellationToken cancellationToken)
    {
        var nameLimit = SparseNameLimit;
        if (nameLimit <= 0) return null;
        var matched = 0;
        foreach (var word in matches)
        {
            matched += BitOperations.PopCount(word);
            if (matched > nameLimit) return null;
        }
        var ids = new int[matched];
        var next = 0;
        for (var word = 0; word < matches.Length; word++)
            for (var bits = matches[word]; bits != 0; bits &= bits - 1)
                ids[next++] = (word << 6) + BitOperations.TrailingZeroCount(bits);

        var rows = _byName.Rows;
        var count = _byName.Count;
        var starts = new int[matched];
        var lengths = new int[matched];

        void Locate(int at)
        {
            var id = ids[at];
            var lo = 0;
            var hi = count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (_names.Compare(_name[rows[mid]], id) < 0) lo = mid + 1; else hi = mid;
            }
            var end = lo;
            while (end < count && _name[rows[end]] == id) end++;
            starts[at] = lo;
            lengths[at] = end - lo;
        }

        if (matched < 64)
        {
            for (var at = 0; at < matched; at++)
            {
                if ((at & 15) == 0) cancellationToken.ThrowIfCancellationRequested();
                Locate(at);
            }
        }
        else
        {
            Parallel.For(0, matched, new ParallelOptions { CancellationToken = cancellationToken }, Locate);
        }

        var total = 0L;
        foreach (var length in lengths) total += length;
        if (total > SparseRowLimit) return null;
        Array.Sort(starts, lengths);
        var subset = new uint[total];
        var filled = 0;
        for (var at = 0; at < matched; at++)
        {
            Array.Copy(rows, starts[at], subset, filled, lengths[at]);
            filled += lengths[at];
        }
        return new RowOrder(subset, filled);
    }

    private RowOrder SortedSubset(RowOrder subset, SortField field)
    {
        var rows = subset.Rows;
        var ordinals = new int[subset.Count];
        var count = 0;
        for (var i = 0; i < subset.Count; i++)
            if (field == SortField.Kind || !IsDirectory(rows[i])) ordinals[count++] = i;
        Comparison<int> compare = field switch
        {
            SortField.Size => (left, right) => { var c = _size[rows[left]].CompareTo(_size[rows[right]]); return c != 0 ? c : left.CompareTo(right); },
            SortField.Modified => (left, right) => { var c = _modified[rows[left]].CompareTo(_modified[rows[right]]); return c != 0 ? c : left.CompareTo(right); },
            _ => (left, right) => { var c = IndexNames.CompareIgnoreCase(KindOf(rows[left]), KindOf(rows[right])); return c != 0 ? c : left.CompareTo(right); }
        };
        Array.Sort(ordinals, 0, count, Comparer<int>.Create(compare));
        var sorted = new uint[count];
        for (var i = 0; i < count; i++) sorted[i] = rows[ordinals[i]];
        return new RowOrder(sorted, count);
    }
}
