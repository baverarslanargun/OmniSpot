using System.Buffers;
using System.Buffers.Binary;
using System.Diagnostics;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Search.Paged;

internal sealed class Bits : IDisposable
{
    readonly ulong[] _words; readonly int _length;
    internal Bits(int maximum) { _length = (maximum + 63) / 64; _words = ArrayPool<ulong>.Shared.Rent(_length); Array.Clear(_words, 0, _length); }
    internal void Add(uint id) => _words[id >> 6] |= 1UL << (int)(id & 63);
    internal bool Has(uint id) => (_words[id >> 6] & (1UL << (int)(id & 63))) != 0;
    internal void Remove(uint id) => _words[id >> 6] &= ~(1UL << (int)(id & 63));
    internal IEnumerable<uint> Values() { for (int word = 0; word < _length; word++) { var value = _words[word]; while (value != 0) { var bit = BitOperations.TrailingZeroCount(value); yield return (uint)(word * 64 + bit); value &= value - 1; } } }
    public void Dispose() => ArrayPool<ulong>.Shared.Return(_words);
}
internal readonly record struct Hit(uint Id, double Score);
internal sealed record Window(long Generation, int Total, IReadOnlyList<SearchResult> Results, long AllocatedBytes = 0);
internal sealed partial class Catalog
{
    internal Dictionary<string, double>? Profile;
    internal int CompareName(uint a, uint b, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var ao = Row(a).Name & NameMask; var bo = Row(b).Name & NameMask;
        if (ao == bo) return 0;
        if (comparison == StringComparison.OrdinalIgnoreCase) { var ar = NameRank(ao); var br = NameRank(bo); if (ar != 0 && br != 0) return ar.CompareTo(br); }
        return CompareNameAt(ao, bo, comparison);
    }
    int NameRank(int offset) => BinaryPrimitives.ReadInt32LittleEndian(Data.Bytes(Image.Names, offset - 4, 4));
    int CompareNameAt(int a, int b, StringComparison comparison = StringComparison.OrdinalIgnoreCase)
    {
        var left = NameAt(a, out var lw); var right = NameAt(b, out var rw);
        if (!lw && !rw && Ascii(left) && Ascii(right))
        { var len = Math.Min(left.Length, right.Length); for (int i = 0; i < len; i++) { var x = left[i]; var y = right[i]; if (comparison == StringComparison.OrdinalIgnoreCase) { if (x is >= 97 and <= 122) x -= 32; if (y is >= 97 and <= 122) y -= 32; } if (x != y) return x.CompareTo(y); } return left.Length.CompareTo(right.Length); }
        Span<char> ac = stackalloc char[1024]; Span<char> bc = stackalloc char[1024]; var an = DecodeName(left, lw, ac); var bn = DecodeName(right, rw, bc); return MemoryExtensions.CompareTo(ac[..an], bc[..bn], comparison);
    }
    static bool Ascii(ReadOnlySpan<byte> bytes) { foreach (var b in bytes) if (b > 127) return false; return true; }
    static int DecodeName(ReadOnlySpan<byte> bytes, bool wide, Span<char> output) { if (wide) { var chars = MemoryMarshal.Cast<byte, char>(bytes); chars.CopyTo(output); return chars.Length; } return Encoding.UTF8.GetChars(bytes, output); }
    void BuildNameRuns() => ApplyNameOrder(CreateNameOrder(default), default);
    sealed class HitOrder(Catalog owner, ItemSort sort) : IComparer<Hit>
    {
        readonly Dictionary<uint, string> _paths = [];
        string PathOf(uint id) { if (!_paths.TryGetValue(id, out var path)) { if (_paths.Count > 2048) _paths.Clear(); path = owner.FullPath(id); _paths[id] = path; } return path; }
        public int Compare(Hit a, Hit b)
        {
            int primary;
            if (sort.Field == SortField.Name) primary = owner.CompareName(a.Id, b.Id);
            else if (sort.Field == SortField.Relevance) primary = a.Score.CompareTo(b.Score);
            else
            {
                var ad = owner.IsDirectory(a.Id); var bd = owner.IsDirectory(b.Id);
                if (sort.Field == SortField.Kind) primary = string.Compare(ad ? "" : System.IO.Path.GetExtension(owner.Name(a.Id)), bd ? "" : System.IO.Path.GetExtension(owner.Name(b.Id)), StringComparison.OrdinalIgnoreCase);
                else { if (ad != bd) return ad ? 1 : -1; primary = ad ? 0 : sort.Field == SortField.Size ? owner.Meta(a.Id).Size.CompareTo(owner.Meta(b.Id).Size) : owner.Meta(a.Id).Modified.CompareTo(owner.Meta(b.Id).Modified); }
            }
            if (primary != 0) return sort.Descending || sort.IsRelevance ? -primary : primary;
            if (sort.Field != SortField.Relevance) { var score = b.Score.CompareTo(a.Score); if (score != 0) return score; }
            var byName = owner.CompareName(a.Id, b.Id); if (byName != 0) return byName;
            var path = string.Compare(PathOf(a.Id), PathOf(b.Id), StringComparison.OrdinalIgnoreCase); return path != 0 ? path : string.CompareOrdinal(PathOf(a.Id), PathOf(b.Id));
        }
    }
    bool Matches(uint id, ItemFilter filter, DateTime now)
    {
        if (!filter.IsActive) return true; var dir = IsDirectory(id); var metadata = filter.Date != FilterDateRange.Any || filter.Size != FilterSizeRange.Any ? Meta(id) : default;
        return filter.Matches(filter.Categories == FileFilterCategory.None ? "" : Name(id), dir, dir ? null : metadata.Size, dir ? null : new DateTime(metadata.Modified, DateTimeKind.Utc).ToLocalTime(), now);
    }
    internal Window Search(string query, int limit = 50, int offset = 0, ResultView? view = null, CancellationToken cancellationToken = default, bool excludeRoot = false)
    {
        var clock = Profile is null ? null : Stopwatch.StartNew(); void Phase(string label) { if (clock is null) return; Profile![label] = clock.Elapsed.TotalMilliseconds; clock.Restart(); }
        view ??= ResultView.SearchDefault; if (limit < 0 || offset < 0) throw new ArgumentOutOfRangeException(); cancellationToken.ThrowIfCancellationRequested();
        var allTokens = _tokenizer.Tokenize(query).ToArray(); if (allTokens.Length == 0) return new(Data.Generation, 0, []);
        var tokens = allTokens.Distinct(StringComparer.Ordinal).ToArray();
        if (tokens.Length > 63) { var legacy = new SearchEngine(_ => new Reader(this), _tokenizer, new BasicScoringStrategy()).Search(query, int.MaxValue, view, cancellationToken); return new(Data.Generation, legacy.Count, legacy.Skip(offset).Take(limit).ToArray()); }
        using var candidates = new Bits(MaxId); var exact = new Dictionary<uint, ulong>();
        for (int t = 0; t < tokens.Length; t++) { cancellationToken.ThrowIfCancellationRequested(); var flag = 1UL << t; FindMatches(tokens[t], 0, id => { candidates.Add(id); exact.TryGetValue(id, out var mask); exact[id] = mask | flag; }, cancellationToken: cancellationToken); }
        Phase("Exact");
        var trailing = allTokens[^1]; using var partial = new Bits(MaxId); if (trailing.Length >= 2) FindMatches(trailing, 1, id => { candidates.Add(id); partial.Add(id); }, cancellationToken: cancellationToken);
        Phase("Partial");
        if (excludeRoot) candidates.Remove(RootId);
        var trailingMask = 1UL << Array.IndexOf(tokens, trailing); var now = DateTime.Now; var count = 0; var history = HasHistory();
        foreach (var id in candidates.Values()) { if ((id & 4095) == 0) cancellationToken.ThrowIfCancellationRequested(); if (!Matches(id, view.Filter, now)) candidates.Remove(id); else count++; }
        Phase("FilterCount");
        double Score(uint id)
        {
            exact.TryGetValue(id, out var mask); var matches = mask | (partial.Has(id) ? trailingMask : 0); var n = BitOperations.PopCount(matches); var exactCount = BitOperations.PopCount(mask);
            var bytes = NameBytes(id, out var wide); var simple = !wide && Ascii(bytes); if (!simple) { exactCount = 0; for (int t = 0; t < tokens.Length; t++) if ((matches & (1UL << t)) != 0 && HasToken(id, tokens[t], StringComparison.Ordinal)) exactCount++; }
            var score = n * 50d + (n == allTokens.Length ? 100 : 0) + exactCount * 25 + ((!simple || bytes.Length == query.Length) && NameEquals(id, query, StringComparison.OrdinalIgnoreCase) ? 200 : 0) + (history ? AliasNext(id | 0x80000000) * 2d : 0);
            return mask == 0 ? score * 0.7 : score;
        }
        if (limit == 0 || count <= offset) return new(Data.Generation, count, []);
        var comparer = new HitOrder(this, view.Sort); List<Hit> selected;
        if (view.Sort.Field == SortField.Name && Data.SortedCount > 0) selected = NameWindow(candidates, Score, offset, limit, view.Sort, comparer);
        else
        {
            var size = Math.Min(count, checked(offset + limit)); var worst = Comparer<Hit>.Create((a, b) => comparer.Compare(b, a)); var queue = new PriorityQueue<Hit, Hit>(worst);
            foreach (var id in candidates.Values()) { var h = new Hit(id, Score(id)); if (queue.Count < size) queue.Enqueue(h, h); else if (comparer.Compare(h, queue.Peek()) < 0) { queue.Dequeue(); queue.Enqueue(h, h); } }
            selected = queue.UnorderedItems.Select(x => x.Element).ToList(); selected.Sort(comparer); selected = selected.Skip(offset).Take(limit).ToList();
        }
        Phase("Select"); cancellationToken.ThrowIfCancellationRequested(); var output = new Window(Data.Generation, count, selected.Select(h => { var item = Item(h.Id); return new SearchResult { Name = item.Name, FullPath = item.FullPath, Score = h.Score, IsDirectory = item.IsDirectory, SizeBytes = item.SizeBytes, LastWriteTime = item.LastWriteTime }; }).ToArray()); Phase("Materialize"); return output;
    }
    List<Hit> NameWindow(Bits candidates, Func<uint, double> score, int offset, int limit, ItemSort sort, IComparer<Hit> finalOrder)
    {
        var heads = new PriorityQueue<(int Run, int At, int End, uint Id), uint>(Comparer<uint>.Create((a, b) => sort.Descending ? -CompareName(a, b) : CompareName(a, b)));
        var extra = candidates.Values().Where(id => (Row(id).Name & Dirty) != 0).ToArray(); Array.Sort(extra, Comparer<uint>.Create((a, b) => sort.Descending ? -CompareName(a, b) : CompareName(a, b)));
        var step = sort.Descending ? -1 : 1;
        void Next(int run, int at, int end)
        { while (at != end) { var id = run < 0 ? extra[at] : Data.Read<uint>(Image.NameOrder, at); if (candidates.Has(id) && (run < 0 || (Row(id).Name & Dirty) == 0)) { heads.Enqueue((run, at, end, id), id); return; } at += run < 0 ? 1 : step; } }
        if (Data.SortedCount > 0) Next(0, sort.Descending ? Data.SortedCount - 1 : 0, sort.Descending ? -1 : Data.SortedCount);
        if (extra.Length > 0) Next(-1, 0, extra.Length); var answer = new List<Hit>(limit); var skipped = 0;
        while (heads.Count > 0 && answer.Count < limit)
        {
            var first = heads.Peek().Id; var group = new List<uint>();
            do { var h = heads.Dequeue(); group.Add(h.Id); Next(h.Run, h.At + (h.Run < 0 ? 1 : step), h.End); } while (heads.Count > 0 && CompareName(first, heads.Peek().Id) == 0);
            if (skipped + group.Count <= offset) { skipped += group.Count; continue; }
            var ranked = group.Select(id => new Hit(id, score(id))).ToList(); ranked.Sort(finalOrder); foreach (var hit in ranked) { if (skipped++ < offset) continue; answer.Add(hit); if (answer.Count == limit) break; }
        }
        return answer;
    }
}
internal sealed class Reader(Catalog catalog, bool ownsCatalog = false, bool excludeRoot = false, bool cachePaths = false) : IQueryCatalogSnapshot, IQuerySnapshotLease, IStandardQuerySnapshot, ILiteralNameQuerySnapshot, ITokenIndexSnapshot
{
    private int _disposed;
    private readonly Dictionary<string, uint>? _paths = cachePaths ? new(StringComparer.OrdinalIgnoreCase) : null;
    private readonly HashSet<string>? _missingPaths = cachePaths ? new(StringComparer.OrdinalIgnoreCase) : null;
    private long _missingRevision = -1;
    public int ItemCount => catalog.Data.EntryCount - (excludeRoot ? 1 : 0); public int TokenCount => checked((int)catalog.Data.Count(Image.Words) - 1);
    internal string RootPath => catalog.Data.RootPath;
    public bool IsTokenIndexReady => TokenIndexing.Enabled && catalog.Data.TermsReady;
    internal IEnumerable<string> Tokens => catalog.TokenNames();
    public ISearchStateReader ForQuery() { ObjectDisposedException.ThrowIf(_disposed != 0, this); return new Reader(catalog.Retain(), true, excludeRoot); }
    public IReadOnlyList<SearchResult> SearchStandard(string query, int limit, ResultView view, CancellationToken ct) => catalog.Search(query, limit, view: view, cancellationToken: ct, excludeRoot: excludeRoot).Results;
    public IReadOnlyList<SearchResult> SearchLiteralNames(string query, int limit, ResultView view, CancellationToken ct) => catalog.SearchLiteralNames(query, limit, view, ct, excludeRoot);
    public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken ct = default) => Materialize(catalog.QueryIds(token, 0, cancellationToken: ct), ct);
    public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken ct = default) => Materialize(catalog.QueryIds(token, 1, cancellationToken: ct), ct);
    public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken ct = default) => Materialize(catalog.QueryIds(token, 2, maxDistance, ct), ct);
    public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken ct = default) => Materialize(catalog.ActiveIds(), ct);
    private SearchItem[] Materialize(IEnumerable<uint> ids, CancellationToken ct) => ids.Where(id => !excludeRoot || id != Catalog.RootId).Select(id => { ct.ThrowIfCancellationRequested(); return catalog.Item(id); }).ToArray();
    internal uint FindPath(string path)
    {
        var root = System.IO.Path.TrimEndingDirectorySeparator(catalog.Data.RootPath);
        path = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(path));
        if (string.Equals(path, root, StringComparison.OrdinalIgnoreCase)) return Catalog.RootId;
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        if (!path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)) return 0;
        if (_missingPaths is not null && _missingRevision != catalog.PathRevision)
        {
            _missingPaths.Clear();
            _missingRevision = catalog.PathRevision;
        }
        if (_missingPaths?.Contains(path) == true) return 0;
        if (Cached(path) is { } cached) return cached;
        var id = Catalog.RootId;
        var current = root;
        foreach (var name in path[prefix.Length..].Split('\\'))
        {
            if (_paths is not null) current = current.TrimEnd('\\') + "\\" + name;
            if (_missingPaths?.Contains(current) == true) return 0;
            id = Cached(current) ?? catalog.FindChild(id, name);
            if (id == 0)
            {
                if (_missingPaths is not null)
                {
                    if (_missingPaths.Count >= 4096) _missingPaths.Clear();
                    _missingPaths.Add(current);
                }
                return 0;
            }
            if (_paths is not null)
            {
                if (_paths.Count >= 4096) _paths.Clear();
                _paths[current] = id;
            }
        }
        return id;
    }
    private uint? Cached(string path)
    {
        if (_paths is null || !_paths.TryGetValue(path, out var id)) return null;
        if (catalog.Alive(id) && string.Equals(catalog.FullPath(id), path, StringComparison.OrdinalIgnoreCase)) return id;
        _paths.Remove(path);
        return null;
    }
    public bool TryGetItem(string path, out SearchItem item) { var id = FindPath(path); if(excludeRoot && id == Catalog.RootId) id = 0; item = id == 0 ? null! : catalog.Item(id); return id != 0; }
    public IReadOnlyCollection<SearchItem> GetRoots(CancellationToken ct = default) => Materialize([Catalog.RootId], ct);
    public IReadOnlyCollection<SearchItem> GetChildren(string parentPath, CancellationToken ct = default) => Materialize(catalog.Children(FindPath(parentPath)), ct);
    public IEnumerable<SearchItem> GetItems(QueryCatalogFilter filter, CancellationToken ct = default)
    {
        foreach (var id in catalog.ActiveIds())
        {
            if(excludeRoot && id == Catalog.RootId) continue;
            ct.ThrowIfCancellationRequested(); var directory = catalog.IsDirectory(id); var meta = catalog.Meta(id);
            if (filter.Matches(directory, directory ? null : meta.Size, directory ? null : new DateTime(meta.Created, DateTimeKind.Utc).ToLocalTime(), directory ? null : new DateTime(meta.Modified, DateTimeKind.Utc).ToLocalTime())) yield return catalog.Item(id);
        }
    }
    public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken ct = default)
    { var id = FindPath(item.FullPath); var list = new List<SearchItem>(); if (id == 0) return list; var queue = new Queue<uint>(catalog.Children(id)); while (queue.TryDequeue(out var at)) { ct.ThrowIfCancellationRequested(); list.Add(catalog.Item(at)); foreach (var child in catalog.Children(at)) queue.Enqueue(child); } return list; }
    public bool ContainsPath(string path) => FindPath(path) != 0;
    public ISearchStateReader WithUpserts(IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer) => throw new NotSupportedException("Use writer transaction");
    public ISearchStateReader WithoutPathAndDescendants(string path) => throw new NotSupportedException("Use writer transaction");
    public ISearchStateReader WithChanges(IEnumerable<string> removedPaths, IEnumerable<FileSystemNode> upserts, ITokenizer tokenizer) => throw new NotSupportedException("Use writer transaction");
    public void Dispose() { if (Interlocked.Exchange(ref _disposed, 1) == 0 && ownsCatalog) catalog.Dispose(); }
}
