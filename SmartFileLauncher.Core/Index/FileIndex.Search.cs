using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Index;

public sealed partial class FileIndex
{
    public IReadOnlyList<SearchResult> Search(string query, int limit, ResultView view, CancellationToken cancellationToken = default) =>
        Search(query, limit, view, null, cancellationToken);

    public IReadOnlyList<SearchResult> Search(string query, int limit, ResultView view, IReadOnlyList<string>? scope,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return [];
        var pattern = IndexNames.FoldQuery(query);
        _lock.EnterReadLock();
        try
        {
            var matches = _names.Match(pattern, cancellationToken);
            var rows = Collect(matches, limit, view, ResolveScope(scope), cancellationToken);
            var results = new SearchResult[rows.Count];
            for (var i = 0; i < rows.Count; i++) results[i] = ToResult(rows[i]);
            return results;
        }
        finally { _lock.ExitReadLock(); }
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
            var name = _name[row];
            if ((matches[name >> 6] & 1UL << (name & 63)) == 0 || row == _root || !InScope(row, scope)) return false;
            if (filter.IsActive)
            {
                var directory = IsDirectory(row);
                if (!filter.Matches(needsName ? _names.Text(name) : "", directory, directory ? null : _size[row],
                        directory ? null : new DateTime(_modified[row], DateTimeKind.Utc).ToLocalTime(), now)) return false;
            }
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
        switch (sort.Field)
        {
            case SortField.Size:
            case SortField.Modified:
            {
                var order = EnsureOrder(sort.Field, cancellationToken);
                var done = sort.Descending
                    ? Backward(order, sort.Field == SortField.Size
                        ? (left, right) => _size[left] == _size[right]
                        : (left, right) => _modified[left] == _modified[right])
                    : Forward(order);
                if (!done) Forward(_byName, directoriesOnly: true);
                break;
            }
            case SortField.Kind:
            {
                var order = EnsureOrder(SortField.Kind, cancellationToken);
                if (sort.Descending) Backward(order, (left, right) => KindOf(left).SequenceEqual(KindOf(right)));
                else Forward(order);
                break;
            }
            case SortField.Name when sort.Descending:
                Backward(_byName, (left, right) => _name[left] == _name[right]);
                break;
            default:
                Forward(_byName);
                break;
        }
        return selected;
    }
}
