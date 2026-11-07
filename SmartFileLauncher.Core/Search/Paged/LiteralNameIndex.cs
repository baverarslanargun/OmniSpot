using System.Diagnostics;
using System.Text;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search.Paged;

internal sealed partial class Catalog
{
    private readonly record struct LiteralNameRow(uint Id, int Block, int Offset, ushort Length, bool Wide, bool Ascii);

    private sealed class LiteralNameIndex(LiteralNameRow[] rows, byte[][] names)
    {
        internal readonly LiteralNameRow[] Rows = rows;
        internal readonly byte[][] Names = names;
        internal ulong[] Grams = [];
        internal int[] BlockStarts = [];
        internal int[] RowBlocks = [];
        internal readonly Dictionary<ItemSort, int[]> Orders = [];
        internal IndexBuildOptions Options = new();
        internal long ChangedSinceBuild;
        internal ReadOnlySpan<byte> Name(LiteralNameRow row) => Names[row.Block].AsSpan(row.Offset, row.Length);
    }

    private const int LiteralNameBlockSize = 1024 * 1024;
    private readonly object _literalPreparationGate = new();
    private LiteralNameIndex? _literalNameIndex;
    private Catalog? _literalBaseline;

    internal void ReuseLiteralNameIndex(Catalog previous)
    {
        var index = Volatile.Read(ref previous._literalNameIndex);
        if (index is null || Data.Mutable ||
            Data.RootFrn != previous.Data.RootFrn || Data.RootPath != previous.Data.RootPath ||
            Data.VolumeIdentity != previous.Data.VolumeIdentity) return;
        if (Data.EntryCount == previous.Data.EntryCount && SameLiteralSource(previous))
        {
            Volatile.Write(ref _literalNameIndex, index);
            return;
        }
        Interlocked.Exchange(ref _literalBaseline, previous.Retain())?.Dispose();
    }

    private bool SameLiteralSource(Catalog previous)
    {
        foreach (var area in new[] { Image.Files, Image.Dirs, Image.Names, Image.Metadata })
        {
            var a = Data.Areas[area];
            var b = previous.Data.Areas[area];
            if (a.Length != b.Length || a.Pages.Count != b.Pages.Count) return false;
            for (var i = 0; i < a.Pages.Count; i++)
                if (!SamePage(a.Pages[i], b.Pages[i])) return false;
        }
        return true;
    }

    private static bool SamePage(Page x, Page y) => ReferenceEquals(x, y) ||
        x.Segment.Path == y.Segment.Path && x.Offset == y.Offset && (x.Hash ?? []).AsSpan().SequenceEqual(y.Hash ?? []);

    private LiteralNameRow CopyLiteralName(uint id, List<byte[]> names, ref int used)
    {
        var name = NameBytes(id, out var wide);
        if (used + name.Length > LiteralNameBlockSize || names.Count == 0)
        {
            names.Add(new byte[LiteralNameBlockSize]);
            used = 0;
        }
        var ascii = !wide && Ascii(name);
        var destination = names[^1].AsSpan(used, name.Length);
        name.CopyTo(destination);
        if (ascii) for (var i = 0; i < destination.Length; i++)
            if (destination[i] is >= 97 and <= 122) destination[i] -= 32;
        var row = new LiteralNameRow(id, names.Count - 1, used, checked((ushort)name.Length), wide, ascii);
        used += name.Length;
        return row;
    }

    private static void TrimLastLiteralBlock(List<byte[]> names, int firstOwned, int used)
    {
        if (names.Count <= firstOwned) return;
        var last = names[^1];
        Array.Resize(ref last, used);
        names[^1] = last;
    }

    internal void PrepareLiteralNameIndex(CancellationToken cancellationToken, IndexBuildOptions? options = null, Action<string, double>? timing = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (Data.Mutable || Volatile.Read(ref _literalNameIndex) is not null) return;
        lock (_literalPreparationGate)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (_literalNameIndex is not null) return;
            var clock = Stopwatch.StartNew();
            if (Volatile.Read(ref _literalBaseline) is { } baseline)
            {
                var derived = DeriveLiteralNameIndex(baseline, cancellationToken);
                Interlocked.Exchange(ref _literalBaseline, null)?.Dispose();
                if (derived is not null)
                {
                    timing?.Invoke("literal-derive", clock.Elapsed.TotalMilliseconds);
                    Volatile.Write(ref _literalNameIndex, derived);
                    return;
                }
                clock.Restart();
            }
            var names = new List<byte[]>();
            var rows = new LiteralNameRow[Data.EntryCount];
            var used = LiteralNameBlockSize;
            var count = 0;
            foreach (var id in ActiveIds())
            {
                if ((count & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                rows[count++] = CopyLiteralName(id, names, ref used);
            }
            if (count != rows.Length) throw new InvalidDataException("Literal katalog kayıt sayısı eşleşmedi.");
            TrimLastLiteralBlock(names, 0, used);
            var index = new LiteralNameIndex(rows, names.ToArray());
            timing?.Invoke("literal-copy", clock.Elapsed.TotalMilliseconds); clock.Restart();
            var build = IndexBuildOptions.Normalize(options);
            var workers = IndexSorter.Sort(rows, (left, right) => CompareLiteralNames(index, left, right), cancellationToken, build);
            timing?.Invoke("literal-sort", clock.Elapsed.TotalMilliseconds); clock.Restart();
            index.Options = build with { AdaptiveSort = false, SortWorkers = workers };
            var paths = new LiteralTiePaths(this);
            string TiePath(uint id) => paths.Of(id);
            long pathTicks = 0, tieSortTicks = 0;
            for (var start = 0; start < rows.Length;)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var end = start + 1;
                while (end < rows.Length && CompareLiteralNames(index, rows[start], rows[end]) == 0)
                {
                    if ((end & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                    end++;
                }
                if (end - start > 1)
                {
                    var stepStarted = Stopwatch.GetTimestamp();
                    var group = new (LiteralNameRow Row, string Path)[end - start];
                    for (var at = start; at < end; at++)
                    {
                        if ((at & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                        group[at - start] = (rows[at], TiePath(rows[at].Id));
                    }
                    pathTicks += Stopwatch.GetTimestamp() - stepStarted;
                    stepStarted = Stopwatch.GetTimestamp();
                    SortLiteral(group, (left, right) =>
                    {
                        var byPath = string.Compare(left.Path, right.Path, StringComparison.OrdinalIgnoreCase);
                        return byPath != 0 ? byPath : string.CompareOrdinal(left.Path, right.Path);
                    }, cancellationToken, index.Options);
                    tieSortTicks += Stopwatch.GetTimestamp() - stepStarted;
                    for (var at = 0; at < group.Length; at++) rows[start + at] = group[at].Row;
                }
                start = end;
            }
            timing?.Invoke("literal-tie-paths", pathTicks * 1000d / Stopwatch.Frequency);
            timing?.Invoke("literal-tie-sorts", tieSortTicks * 1000d / Stopwatch.Frequency);
            timing?.Invoke("literal-ties", clock.Elapsed.TotalMilliseconds); clock.Restart();
            index.Grams = BuildLiteralGramFilter(index, cancellationToken);
            timing?.Invoke("literal-grams", clock.Elapsed.TotalMilliseconds);
            cancellationToken.ThrowIfCancellationRequested();
            Volatile.Write(ref _literalNameIndex, index);
        }
    }

    private static int CompareLiteralNames(LiteralNameIndex index, LiteralNameRow left, LiteralNameRow right)
    {
        var a = index.Name(left);
        var b = index.Name(right);
        if (left.Ascii && right.Ascii) return a.SequenceCompareTo(b);
        Span<char> ac = stackalloc char[1024];
        Span<char> bc = stackalloc char[1024];
        return MemoryExtensions.CompareTo(ac[..DecodeName(a, left.Wide, ac)],
            bc[..DecodeName(b, right.Wide, bc)], StringComparison.OrdinalIgnoreCase);
    }

    private static void SortLiteral<T>(T[] items, Comparison<T> compare, CancellationToken cancellationToken, IndexBuildOptions? options = null)
    {
        IndexSorter.Sort(items, compare, cancellationToken, items.Length < 8192 && options is not null
            ? options with { AdaptiveSort = false, SortWorkers = 1 } : options);
    }

    private int[]? LiteralOrder(LiteralNameIndex index, ItemSort sort, CancellationToken cancellationToken)
    {
        if (sort.IsRelevance || sort == ItemSort.NameAscending) return null;
        lock (index.Orders)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (index.Orders.TryGetValue(sort, out var existing)) return existing;
            var order = new int[index.Rows.Length];
            var keys = sort.Field is SortField.Size or SortField.Modified ? new long[order.Length] : null;
            var kinds = sort.Field == SortField.Kind ? new string[order.Length] : null;
            for (var i = 0; i < order.Length; i++)
            {
                if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                order[i] = i;
                var row = index.Rows[i];
                if (keys is not null && !IsDirectory(row.Id))
                {
                    var meta = Meta(row.Id);
                    keys[i] = sort.Field == SortField.Size ? meta.Size : meta.Modified;
                }
                if (kinds is not null) kinds[i] = IsDirectory(row.Id) ? "" : Path.GetExtension(Name(row.Id));
            }
            SortLiteral(order, (a, b) =>
            {
                int primary;
                if (keys is not null)
                {
                    var ad = IsDirectory(index.Rows[a].Id);
                    var bd = IsDirectory(index.Rows[b].Id);
                    if (ad != bd) return ad ? 1 : -1;
                    primary = keys[a].CompareTo(keys[b]);
                }
                else if (kinds is not null) primary = string.Compare(kinds[a], kinds[b], StringComparison.OrdinalIgnoreCase);
                else primary = CompareLiteralNames(index, index.Rows[a], index.Rows[b]);
                return primary != 0 ? sort.Descending ? -primary : primary : a.CompareTo(b);
            }, cancellationToken, index.Options);
            index.Orders.Add(sort, order);
            return order;
        }
    }

    private IReadOnlyList<SearchResult> SearchPreparedLiteralNames(LiteralNameIndex index, string query,
        int limit, ResultView view, CancellationToken cancellationToken, bool excludeRoot)
    {
        var ascii = query.All(character => character <= 127);
        var pattern = ascii ? Encoding.ASCII.GetBytes(query) : [];
        for (var i = 0; i < pattern.Length; i++) pattern[i] = LowerAscii(pattern[i]);
        var order = LiteralOrder(index, view.Sort, cancellationToken);
        bool[]? candidateBlocks = null;
        if (pattern.Length >= 3)
        {
            var candidates = CandidateLiteralGramBlocks(pattern, index.Grams, cancellationToken);
            var blockCount = index.Grams.Length / LiteralGramWords;
            if ((long)candidates.Count * 5 < (long)blockCount * 4)
            {
                candidateBlocks = new bool[blockCount];
                foreach (var block in candidates) candidateBlocks[block] = true;
            }
        }
        var selected = new List<uint>(Math.Min(limit, index.Rows.Length));
        var now = DateTime.Now;
        Span<char> characters = stackalloc char[1024];
        for (var at = 0; at < index.Rows.Length; at++)
        {
            if ((at & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var position = order is null ? at : order[at];
            if (candidateBlocks is not null && !candidateBlocks[LiteralBlockOf(index, position)])
            {
                if (order is null) at = LiteralBlockEnd(index, LiteralBlockOf(index, position)) - 1;
                continue;
            }
            var row = index.Rows[position];
            if (excludeRoot && row.Id == RootId) continue;
            var name = index.Name(row);
            var found = ascii && !row.Wide ? ContainsAsciiIgnoreCase(name, pattern)
                : MemoryExtensions.Contains(characters[..DecodeName(name, row.Wide, characters)],
                    query.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (!found || !Matches(row.Id, view.Filter, now)) continue;
            selected.Add(row.Id);
            if (selected.Count == limit) break;
        }
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<SearchResult>(selected.Count);
        foreach (var id in selected)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = Item(id);
            results.Add(new SearchResult
            {
                Name = item.Name, FullPath = item.FullPath, Score = 0,
                IsDirectory = item.IsDirectory, SizeBytes = item.SizeBytes, LastWriteTime = item.LastWriteTime
            });
        }
        return results;
    }
}
