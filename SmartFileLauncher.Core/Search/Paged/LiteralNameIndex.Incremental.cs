using System.Text;
using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.Core.Search.Paged;

internal sealed partial class Catalog
{
    private const int LiteralGramSplitRows = 4 * LiteralGramRows;

    private sealed class LiteralTiePaths(Catalog owner)
    {
        private readonly Dictionary<uint, string> _parents = [];
        private readonly List<uint> _chain = new(32);

        private string Parent(uint id)
        {
            if (_parents.Count >= 32768) _parents.Clear();
            if (_parents.TryGetValue(id, out var cached)) return cached;
            _chain.Clear();
            var at = id;
            var prefix = owner.Data.RootPath.TrimEnd('\\');
            while (at != RootId)
            {
                if (!owner.Alive(at) || _chain.Count > 1024) throw new InvalidDataException("Parent cycle/missing");
                if (_parents.TryGetValue(at, out cached)) { prefix = cached; break; }
                _chain.Add(at);
                at = (uint)owner.Row(at).Parent;
            }
            for (var i = _chain.Count - 1; i >= 0; i--)
            {
                prefix += "\\" + owner.Name(_chain[i]);
                _parents[_chain[i]] = prefix;
            }
            return prefix;
        }

        internal string Of(uint id)
        {
            if (id == RootId) return owner.Data.RootPath;
            var parent = Parent((uint)owner.Row(id).Parent);
            var name = owner.NameBytes(id, out var wide);
            var length = wide ? name.Length / 2 : Encoding.UTF8.GetCharCount(name);
            return string.Create(parent.Length + 1 + length, (Owner: owner, Id: id, Parent: parent), static (span, state) =>
            {
                state.Parent.CopyTo(span);
                span[state.Parent.Length] = '\\';
                var bytes = state.Owner.NameBytes(state.Id, out var utf16);
                DecodeName(bytes, utf16, span[(state.Parent.Length + 1)..]);
            });
        }
    }

    private static int CompareLiteralPaths(string left, string right)
    {
        var byPath = string.Compare(left, right, StringComparison.OrdinalIgnoreCase);
        return byPath != 0 ? byPath : string.CompareOrdinal(left, right);
    }

    private static int LiteralBlockOf(LiteralNameIndex index, int position) =>
        index.RowBlocks.Length == 0 ? position / LiteralGramRows : index.RowBlocks[position];

    private static int LiteralBlockEnd(LiteralNameIndex index, int block) =>
        index.BlockStarts.Length == 0 ? Math.Min(index.Rows.Length, (block + 1) * LiteralGramRows) : index.BlockStarts[block + 1];

    private LiteralNameIndex? DeriveLiteralNameIndex(Catalog previous, CancellationToken cancellationToken)
    {
        var old = Volatile.Read(ref previous._literalNameIndex);
        if (old is null || old.Rows.Length == 0) return null;
        var limit = Math.Max(4096, old.Rows.Length / 4);
        var changed = new HashSet<uint>();
        if (!CollectChangedLiteralIds(previous, Image.Files, false, changed, limit, cancellationToken) ||
            !CollectChangedLiteralIds(previous, Image.Dirs, true, changed, limit, cancellationToken)) return null;
        if (changed.Count == 0) return old;
        if (old.ChangedSinceBuild + changed.Count > limit) return null;

        var removed = new ulong[(Math.Max(MaxId, previous.MaxId) >> 6) + 2];
        bool Removed(uint id) => (removed[id >> 6] & 1UL << (int)(id & 63)) != 0;
        var names = new List<byte[]>(old.Names);
        var used = LiteralNameBlockSize;
        var added = new List<LiteralNameRow>(changed.Count);
        foreach (var id in changed)
        {
            cancellationToken.ThrowIfCancellationRequested();
            removed[id >> 6] |= 1UL << (int)(id & 63);
            if (Alive(id)) added.Add(CopyLiteralName(id, names, ref used));
        }
        TrimLastLiteralBlock(names, old.Names.Length, used);
        var probe = new LiteralNameIndex(old.Rows, names.ToArray());

        var paths = new LiteralTiePaths(this);
        var addedPaths = new Dictionary<uint, string>();
        string AddedPath(uint id)
        {
            if (!addedPaths.TryGetValue(id, out var path)) addedPaths[id] = path = paths.Of(id);
            return path;
        }
        var addedRows = added.ToArray();
        Array.Sort(addedRows, (left, right) =>
        {
            var byName = CompareLiteralNames(probe, left, right);
            return byName != 0 ? byName : CompareLiteralPaths(AddedPath(left.Id), AddedPath(right.Id));
        });

        var insertion = new int[addedRows.Length];
        var floor = 0;
        for (var i = 0; i < addedRows.Length; i++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var row = addedRows[i];
            var lo = floor;
            var hi = old.Rows.Length;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (CompareLiteralNames(probe, old.Rows[mid], row) < 0) lo = mid + 1; else hi = mid;
            }
            var end = old.Rows.Length;
            var upper = lo;
            while (upper < end)
            {
                var mid = (upper + end) >>> 1;
                if (CompareLiteralNames(probe, old.Rows[mid], row) <= 0) upper = mid + 1; else end = mid;
            }
            if (lo < upper)
            {
                var path = AddedPath(row.Id);
                var a = lo;
                var b = upper;
                while (a < b)
                {
                    var mid = (a + b) >>> 1;
                    var at = mid;
                    while (at < b && Removed(old.Rows[at].Id)) at++;
                    if (at == b) { b = mid; continue; }
                    if (CompareLiteralPaths(paths.Of(old.Rows[at].Id), path) < 0) a = at + 1; else b = mid;
                }
                lo = a;
            }
            insertion[i] = floor = Math.Max(floor, lo);
        }

        var removedCount = 0;
        foreach (var row in old.Rows) if (Removed(row.Id)) removedCount++;
        var rows = new LiteralNameRow[old.Rows.Length - removedCount + addedRows.Length];
        if (rows.Length != Data.EntryCount) return null;
        var remap = new int[old.Rows.Length];
        var source = new int[rows.Length];
        var next = 0;
        var from = 0;
        for (var i = 0; i <= addedRows.Length; i++)
        {
            var until = i < addedRows.Length ? insertion[i] : old.Rows.Length;
            for (; from < until; from++)
            {
                if ((from & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (Removed(old.Rows[from].Id)) { remap[from] = -1; continue; }
                remap[from] = next;
                source[next] = from;
                rows[next++] = old.Rows[from];
            }
            if (i < addedRows.Length)
            {
                source[next] = -1 - i;
                rows[next++] = addedRows[i];
            }
        }

        var index = new LiteralNameIndex(rows, probe.Names)
        {
            Options = old.Options,
            ChangedSinceBuild = old.ChangedSinceBuild + changed.Count
        };
        DeriveLiteralGrams(old, index, source, insertion, cancellationToken);
        KeyValuePair<ItemSort, int[]>[] orders;
        lock (old.Orders) orders = old.Orders.ToArray();
        foreach (var (sort, order) in orders)
            index.Orders[sort] = DeriveLiteralOrder(index, sort, order, remap, source, cancellationToken);
        return index;
    }

    private bool CollectChangedLiteralIds(Catalog previous, int area, bool directories, HashSet<uint> changed, int limit,
        CancellationToken cancellationToken)
    {
        var current = Data.Areas[area];
        var before = previous.Data.Areas[area];
        var perPage = Segment.PageSize / current.RowSize;
        var rowCount = Math.Max(Data.Count(area), previous.Data.Count(area));
        var pages = Math.Max(current.Pages.Count, before.Pages.Count);
        for (var page = 0; page < pages; page++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (page < current.Pages.Count && page < before.Pages.Count && SamePage(current.Pages[page], before.Pages[page])) continue;
            var end = Math.Min(rowCount, (long)(page + 1) * perPage);
            for (var row = Math.Max(1L, (long)page * perPage); row < end; row++)
            {
                var id = checked((uint)(row * 2 + (directories ? 1 : 0)));
                var was = previous.Alive(id);
                var now = Alive(id);
                if (!was && !now) continue;
                if (was && now)
                {
                    var a = previous.Row(id);
                    var b = Row(id);
                    if (a.Frn == b.Frn && a.Parent == b.Parent && a.Meta == b.Meta && (a.Name & NameMask) == (b.Name & NameMask)) continue;
                }
                changed.Add(id);
                if (changed.Count > limit) return false;
            }
        }
        return true;
    }

    private static void DeriveLiteralGrams(LiteralNameIndex old, LiteralNameIndex index, int[] source, int[] insertion,
        CancellationToken cancellationToken)
    {
        var oldBlocks = old.Grams.Length / LiteralGramWords;
        var sizes = new int[oldBlocks];
        for (var at = 0; at < index.Rows.Length; at++)
        {
            var origin = source[at];
            var position = origin >= 0 ? origin : insertion[-1 - origin];
            sizes[position < old.Rows.Length ? LiteralBlockOf(old, position) : oldBlocks - 1]++;
        }
        var blockCount = 0;
        foreach (var size in sizes)
            blockCount += size == 0 ? 0 : size > LiteralGramSplitRows ? (size + LiteralGramRows - 1) / LiteralGramRows : 1;
        var grams = new ulong[checked(blockCount * LiteralGramWords)];
        var starts = new int[blockCount + 1];
        var rowBlocks = new int[index.Rows.Length];
        var next = 0;
        var start = 0;
        for (var block = 0; block < oldBlocks; block++)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var size = sizes[block];
            if (size == 0) continue;
            if (size > LiteralGramSplitRows)
            {
                for (var offset = 0; offset < size; offset += LiteralGramRows)
                {
                    starts[next] = start + offset;
                    var count = Math.Min(LiteralGramRows, size - offset);
                    for (var row = start + offset; row < start + offset + count; row++)
                    {
                        rowBlocks[row] = next;
                        AddLiteralGrams(grams, next * LiteralGramWords, index, index.Rows[row]);
                    }
                    next++;
                }
            }
            else
            {
                starts[next] = start;
                Array.Copy(old.Grams, block * LiteralGramWords, grams, next * LiteralGramWords, LiteralGramWords);
                for (var row = start; row < start + size; row++)
                {
                    rowBlocks[row] = next;
                    if (source[row] < 0) AddLiteralGrams(grams, next * LiteralGramWords, index, index.Rows[row]);
                }
                next++;
            }
            start += size;
        }
        starts[next] = start;
        index.Grams = grams;
        index.BlockStarts = starts;
        index.RowBlocks = rowBlocks;
    }

    private int[] DeriveLiteralOrder(LiteralNameIndex index, ItemSort sort, int[] previous, int[] remap, int[] source,
        CancellationToken cancellationToken)
    {
        var kept = new int[previous.Length];
        var count = 0;
        foreach (var position in previous)
        {
            var mapped = remap[position];
            if (mapped >= 0) kept[count++] = mapped;
        }
        var added = new List<int>();
        for (var at = 0; at < source.Length; at++) if (source[at] < 0) added.Add(at);
        added.Sort((left, right) => CompareLiteralOrder(index, sort, left, right));
        var order = new int[count + added.Count];
        var next = 0;
        var from = 0;
        foreach (var position in added)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var lo = from;
            var hi = count;
            while (lo < hi)
            {
                var mid = (lo + hi) >>> 1;
                if (CompareLiteralOrder(index, sort, kept[mid], position) < 0) lo = mid + 1; else hi = mid;
            }
            Array.Copy(kept, from, order, next, lo - from);
            next += lo - from;
            from = lo;
            order[next++] = position;
        }
        Array.Copy(kept, from, order, next, count - from);
        return order;
    }

    private int CompareLiteralOrder(LiteralNameIndex index, ItemSort sort, int a, int b)
    {
        var left = index.Rows[a];
        var right = index.Rows[b];
        int primary;
        if (sort.Field is SortField.Size or SortField.Modified)
        {
            var leftDirectory = IsDirectory(left.Id);
            var rightDirectory = IsDirectory(right.Id);
            if (leftDirectory != rightDirectory) return leftDirectory ? 1 : -1;
            primary = LiteralOrderKey(left.Id, sort.Field).CompareTo(LiteralOrderKey(right.Id, sort.Field));
        }
        else if (sort.Field == SortField.Kind)
            primary = string.Compare(LiteralKind(left.Id), LiteralKind(right.Id), StringComparison.OrdinalIgnoreCase);
        else primary = CompareLiteralNames(index, left, right);
        return primary != 0 ? sort.Descending ? -primary : primary : a.CompareTo(b);
    }

    private long LiteralOrderKey(uint id, SortField field)
    {
        if (IsDirectory(id)) return 0;
        var meta = Meta(id);
        return field == SortField.Size ? meta.Size : meta.Modified;
    }

    private string LiteralKind(uint id) => IsDirectory(id) ? "" : Path.GetExtension(Name(id));
}
