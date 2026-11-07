using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Search.Paged;

namespace SmartFileLauncher.Core.Index;

public sealed partial class FileIndex
{
    private sealed class RowOrder(uint[] rows, int count)
    {
        internal uint[] Rows = rows;
        internal int Count = count;
    }

    private RowOrder _byName = new([], 0);
    private RowOrder? _bySize;
    private RowOrder? _byModified;
    private RowOrder? _byKind;
    private readonly object _orderGate = new();

    [ThreadStatic] private static uint[]? _leftChain;
    [ThreadStatic] private static uint[]? _rightChain;

    private void RebuildOrders(CancellationToken cancellationToken)
    {
        var ids = new int[_names.Count];
        for (var i = 0; i < ids.Length; i++) ids[i] = i;
        IndexSorter.Sort(ids, (left, right) => _names.Compare(left, right), cancellationToken);
        var rank = new uint[ids.Length];
        for (var i = 0; i < ids.Length; i++) rank[ids[i]] = (uint)i;
        var folders = FolderRanks(rank, cancellationToken);
        var rows = new uint[_count - _dead];
        var keys = new ulong[rows.Length];
        var at = 0;
        for (var row = 0u; row < _count; row++)
        {
            if (!IsAlive(row)) continue;
            rows[at] = row;
            var parent = _parent[row];
            keys[at++] = (ulong)rank[_name[row]] << 32 | (parent == NoRow ? 0u : folders[parent] + 1);
        }
        cancellationToken.ThrowIfCancellationRequested();
        Array.Sort(keys, rows);
        for (var start = 0; start < keys.Length;)
        {
            var end = start + 1;
            while (end < keys.Length && keys[end] == keys[start]) end++;
            if (end - start > 1) Array.Sort(rows, start, end - start);
            start = end;
        }
        _byName = new RowOrder(WithSlack(rows), rows.Length);
        _byKind = null;
        _bySize = BuildOrder(SortField.Size, cancellationToken);
        _byModified = BuildOrder(SortField.Modified, cancellationToken);
    }

    private uint[] FolderRanks(uint[] nameRank, CancellationToken cancellationToken)
    {
        var offsets = new int[_count + 1];
        for (var row = 0u; row < _count; row++)
            if (IsAlive(row) && IsDirectory(row) && row != _root && _parent[row] != NoRow) offsets[_parent[row] + 1]++;
        for (var i = 1; i < offsets.Length; i++) offsets[i] += offsets[i - 1];
        var children = new ulong[offsets[^1]];
        var fill = (int[])offsets.Clone();
        for (var row = 0u; row < _count; row++)
            if (IsAlive(row) && IsDirectory(row) && row != _root && _parent[row] != NoRow)
                children[fill[_parent[row]]++] = (ulong)nameRank[_name[row]] << 32 | row;
        cancellationToken.ThrowIfCancellationRequested();
        var ranks = new uint[_count];
        var stack = new Stack<uint>();
        stack.Push(_root);
        var next = 0u;
        while (stack.TryPop(out var folder))
        {
            ranks[folder] = next++;
            var segment = children.AsSpan(offsets[folder], offsets[folder + 1] - offsets[folder]);
            segment.Sort();
            for (var i = segment.Length - 1; i >= 0; i--) stack.Push((uint)segment[i]);
        }
        return ranks;
    }

    private static uint[] WithSlack(uint[] rows)
    {
        var result = new uint[rows.Length + Math.Max(1024, rows.Length / 64)];
        rows.CopyTo(result, 0);
        return result;
    }

    private int CompareByName(uint left, uint right)
    {
        if (left == right) return 0;
        var byName = _names.Compare(_name[left], _name[right]);
        return byName != 0 ? byName : ComparePlacement(left, right);
    }

    private int ComparePlacement(uint left, uint right)
    {
        var leftParent = _parent[left];
        var rightParent = _parent[right];
        if (leftParent == rightParent) return left.CompareTo(right);
        if (leftParent == NoRow) return -1;
        if (rightParent == NoRow) return 1;
        var leftDepth = Chain(leftParent, ref _leftChain);
        var rightDepth = Chain(rightParent, ref _rightChain);
        var a = _leftChain!;
        var b = _rightChain!;
        for (var level = 0; level < Math.Min(leftDepth, rightDepth); level++)
        {
            if (a[level] == b[level]) continue;
            var byName = _names.Compare(_name[a[level]], _name[b[level]]);
            return byName != 0 ? byName : a[level].CompareTo(b[level]);
        }
        return leftDepth.CompareTo(rightDepth);
    }

    private int Chain(uint folder, ref uint[]? buffer)
    {
        buffer ??= new uint[64];
        var depth = 0;
        for (var at = folder; at != _root && at != NoRow; at = _parent[at])
        {
            if (depth == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            if (depth > 65536) throw new InvalidDataException("Klasör zinciri döngüsel.");
            buffer[depth++] = at;
        }
        Array.Reverse(buffer, 0, depth);
        return depth;
    }

    private ReadOnlySpan<byte> KindOf(uint row) => IsDirectory(row) ? [] : _names.FoldedExtension(_name[row]);

    private int CompareBySize(uint left, uint right)
    {
        var bySize = _size[left].CompareTo(_size[right]);
        return bySize != 0 ? bySize : CompareByName(left, right);
    }

    private int CompareByModified(uint left, uint right)
    {
        var byModified = _modified[left].CompareTo(_modified[right]);
        return byModified != 0 ? byModified : CompareByName(left, right);
    }

    private int CompareByKind(uint left, uint right)
    {
        var byKind = KindOf(left).SequenceCompareTo(KindOf(right));
        return byKind != 0 ? byKind : CompareByName(left, right);
    }

    private RowOrder EnsureOrder(SortField field, CancellationToken cancellationToken)
    {
        var existing = field switch
        {
            SortField.Size => Volatile.Read(ref _bySize),
            SortField.Modified => Volatile.Read(ref _byModified),
            SortField.Kind => Volatile.Read(ref _byKind),
            _ => _byName
        };
        if (existing is not null) return existing;
        lock (_orderGate)
        {
            existing = field switch
            {
                SortField.Size => _bySize,
                SortField.Modified => _byModified,
                _ => _byKind
            };
            if (existing is not null) return existing;
            var order = BuildOrder(field, cancellationToken);
            switch (field)
            {
                case SortField.Size: Volatile.Write(ref _bySize, order); break;
                case SortField.Modified: Volatile.Write(ref _byModified, order); break;
                default: Volatile.Write(ref _byKind, order); break;
            }
            return order;
        }
    }

    private RowOrder BuildOrder(SortField field, CancellationToken cancellationToken)
    {
        var position = new int[_count];
        for (var i = 0; i < _byName.Count; i++) position[_byName.Rows[i]] = i;
        var rows = new List<uint>(_byName.Count);
        for (var i = 0; i < _byName.Count; i++)
        {
            var row = _byName.Rows[i];
            if (field == SortField.Kind || !IsDirectory(row)) rows.Add(row);
        }
        var values = rows.ToArray();
        Comparison<uint> compare = field switch
        {
            SortField.Size => (left, right) => { var c = _size[left].CompareTo(_size[right]); return c != 0 ? c : position[left].CompareTo(position[right]); },
            SortField.Modified => (left, right) => { var c = _modified[left].CompareTo(_modified[right]); return c != 0 ? c : position[left].CompareTo(position[right]); },
            _ => (left, right) => { var c = KindOf(left).SequenceCompareTo(KindOf(right)); return c != 0 ? c : position[left].CompareTo(position[right]); }
        };
        IndexSorter.Sort(values, compare, cancellationToken);
        return new RowOrder(WithSlack(values), values.Length);
    }

    private static void MergeOrder(RowOrder order, ulong[] removed, bool anyRemoved, uint[] inserts, Comparison<uint> compare)
    {
        var rows = order.Rows;
        var kept = order.Count;
        if (anyRemoved)
        {
            kept = 0;
            for (var read = 0; read < order.Count; read++)
            {
                var row = rows[read];
                if ((removed[row >> 6] & 1UL << (int)(row & 63)) == 0) rows[kept++] = row;
            }
        }
        if (inserts.Length == 0)
        {
            order.Count = kept;
            return;
        }
        Array.Sort(inserts, compare);
        var points = new int[inserts.Length];
        var floor = 0;
        for (var i = 0; i < inserts.Length; i++)
        {
            var lo = floor;
            var hi = kept;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (compare(rows[mid], inserts[i]) < 0) lo = mid + 1; else hi = mid;
            }
            points[i] = floor = lo;
        }
        var total = kept + inserts.Length;
        if (total > rows.Length)
        {
            var grown = new uint[total + Math.Max(1024, total / 64)];
            Array.Copy(rows, grown, kept);
            rows = grown;
        }
        var target = total - 1;
        var source = kept - 1;
        for (var i = inserts.Length - 1; i >= 0; i--)
        {
            while (source >= points[i]) rows[target--] = rows[source--];
            rows[target--] = inserts[i];
        }
        order.Rows = rows;
        order.Count = total;
    }
}
