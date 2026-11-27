using System.Buffers;
using System.Runtime.InteropServices;
using System.Text;
using System.Text.Unicode;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

internal sealed class FileIndexReader(IReadOnlyList<(FileIndex Index, IReadOnlyList<string>? Scope)> volumes) :
    IQueryCatalogSnapshot, ILiteralNameQuerySnapshot, ILiteralNamePageSnapshot, IQuerySnapshotLease, ITokenIndexSnapshot, ITokenPartialSnapshot,
    ITokenSummaryReader, ILiteralNameSuggestionSnapshot
{
    public TokenSummary SummarizeToken(string token, int sampleCount, CancellationToken cancellationToken = default)
    {
        var count = 0;
        var samples = new List<string>(Math.Max(0, sampleCount));
        foreach (var (index, scope) in volumes)
        {
            var summary = index.SummarizeToken(token, sampleCount - samples.Count, scope, cancellationToken);
            count += summary.Count;
            samples.AddRange(summary.SampleNames);
        }
        return new TokenSummary(count, samples);
    }

    public int ItemCount => volumes.Sum(volume => volume.Index.Count);

    public int TokenCount => 0;

    public bool IsTokenIndexReady => true;

    public ISearchStateReader ForQuery() => this;

    public IReadOnlyList<SearchResult> SearchLiteralNames(string query, int limit, ResultView view, CancellationToken cancellationToken) =>
        SearchLiteralNames(query, 0, limit, view, cancellationToken);

    public IReadOnlyList<SearchResult> SearchLiteralNames(string query, int offset, int limit, ResultView view, CancellationToken cancellationToken)
    {
        if (volumes.Count == 1) return volumes[0].Index.Search(query, offset, limit, view, volumes[0].Scope, cancellationToken);
        var window = (int)Math.Min(int.MaxValue, (long)offset + limit);
        var ranked = view.Sort.IsRelevance && view.LiteralRanking is not null;
        var ordered = ranked
            ? volumes.OrderBy(volume => volume.Index.RootPath, StringComparer.OrdinalIgnoreCase)
                .ThenBy(volume => volume.Index.RootPath, StringComparer.Ordinal)
            : (IEnumerable<(FileIndex Index, IReadOnlyList<string>? Scope)>)volumes;
        var lists = ordered.Select(volume => volume.Index.Search(query, 0, window, view, volume.Scope, cancellationToken)).ToArray();
        return Merge(lists, ranked ? new RankedVolumeOrder() : SortedResultOrder.For(view.Sort), offset, limit);
    }

    private static IReadOnlyList<SearchResult> Merge(IReadOnlyList<SearchResult>[] lists, IComparer<SearchResult> order, int offset, int limit)
    {
        var heads = new int[lists.Length];
        var output = new List<SearchResult>(Math.Min(limit, 1024));
        var skipped = 0;
        while (output.Count < limit)
        {
            var best = -1;
            for (var i = 0; i < lists.Length; i++)
                if (heads[i] < lists[i].Count &&
                    (best < 0 || order.Compare(lists[i][heads[i]], lists[best][heads[best]]) < 0)) best = i;
            if (best < 0) break;
            var next = lists[best][heads[best]++];
            if (skipped < offset) skipped++;
            else output.Add(next);
        }
        return output;
    }

    private sealed class RankedVolumeOrder : IComparer<SearchResult>
    {
        private readonly record struct NameKey(byte[] Folded, ReadOnlyMemory<byte> Original, bool Wide);

        private readonly Dictionary<string, NameKey> _keys = new(StringComparer.Ordinal);

        public int Compare(SearchResult? left, SearchResult? right)
        {
            if (left == right) return 0;
            if (left is null) return 1;
            if (right is null) return -1;

            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0) return byScore;
            if (string.Equals(left.Name, right.Name, StringComparison.Ordinal)) return 0;
            if (Ascii.IsValid(left.Name) && Ascii.IsValid(right.Name))
            {
                var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
                return byName != 0 ? byName : string.CompareOrdinal(left.Name, right.Name);
            }

            var a = GetKey(left.Name);
            var b = GetKey(right.Name);
            var byFolded = IndexNames.CompareIgnoreCase(a.Folded, b.Folded);
            if (byFolded != 0) return byFolded;
            var byOriginal = a.Original.Span.SequenceCompareTo(b.Original.Span);
            return byOriginal != 0 ? byOriginal : a.Wide.CompareTo(b.Wide);
        }

        private NameKey GetKey(string name)
        {
            if (_keys.TryGetValue(name, out var key)) return key;
            var buffer = new byte[Encoding.UTF8.GetMaxByteCount(name.Length)];
            var wide = Utf8.FromUtf16(name, buffer, out _, out var written, replaceInvalidSequences: false) != OperationStatus.Done;
            ReadOnlyMemory<byte> original = wide ? MemoryMarshal.AsBytes(name.AsSpan()).ToArray() : buffer.AsMemory(0, written);
            key = new NameKey(IndexNames.FoldQuery(name), original, wide);
            _keys.Add(name, key);
            return key;
        }
    }

    public int CountLiteralNames(string query, ResultView view, CancellationToken cancellationToken) =>
        volumes.Sum(volume => volume.Index.CountMatches(query, view, volume.Scope, cancellationToken));

    public string? SuggestLiteralName(string query, ResultView view, CancellationToken cancellationToken)
    {
        (string Query, int Count)? best = null;
        foreach (var (index, scope) in volumes)
            if (index.SuggestQuery(query, view, scope, cancellationToken) is { } found && (best is null || found.Count > best.Value.Count))
                best = found;
        return best?.Query;
    }

    public bool TryGetItem(string path, out SearchItem item)
    {
        foreach (var (index, scope) in volumes)
            if (InScope(path, scope) && index.TryGetItem(path, out item)) return true;
        item = null!;
        return false;
    }

    public bool ContainsPath(string path) => TryGetItem(path, out _);

    public IReadOnlyCollection<SearchItem> GetRoots(CancellationToken cancellationToken = default)
    {
        var roots = new List<SearchItem>();
        foreach (var (index, scope) in volumes)
        {
            if (scope is null or { Count: 0 }) { roots.Add(index.RootItem()); continue; }
            foreach (var path in scope)
                if (index.TryGetItem(path, out var item)) roots.Add(item);
        }
        return roots;
    }

    public IReadOnlyCollection<SearchItem> GetChildren(string parentPath, CancellationToken cancellationToken = default)
    {
        foreach (var (index, scope) in volumes)
            if (InScope(parentPath, scope) && index.TryGetItem(parentPath, out _))
                return index.Children(parentPath, cancellationToken);
        return [];
    }

    public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken cancellationToken = default) =>
        volumes.SelectMany(volume => volume.Index.AllItems(cancellationToken).Where(item => InScope(item.FullPath, volume.Scope))).ToArray();

    public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken cancellationToken = default)
    {
        foreach (var (index, scope) in volumes)
            if (InScope(item.FullPath, scope) && index.TryGetItem(item.FullPath, out _))
                return index.Descendants(item.FullPath, cancellationToken);
        return [];
    }

    public IEnumerable<SearchItem> GetItems(QueryCatalogFilter filter, CancellationToken cancellationToken = default)
    {
        foreach (var (index, scope) in volumes)
        foreach (var item in index.Items(filter, scope, cancellationToken))
            yield return item;
    }

    public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken cancellationToken = default) =>
        Tokens(token, TokenMatch.Exact, 0, cancellationToken);

    public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken cancellationToken = default) =>
        Tokens(token, TokenMatch.Partial, 0, cancellationToken);

    public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken cancellationToken = default) =>
        Tokens(token, TokenMatch.Fuzzy, maxDistance, cancellationToken);

    public IReadOnlyList<(SearchItem Item, bool Exact)> GetWithPartial(
        string token, QueryCatalogFilter? filter, out bool found, CancellationToken cancellationToken)
    {
        if (volumes.Count == 1)
            return volumes[0].Index.TokenItemsWithPartial(token, volumes[0].Scope, filter, out found, cancellationToken);

        var results = new List<(SearchItem Item, bool Exact)>();
        found = false;
        foreach (var (index, scope) in volumes)
        {
            results.AddRange(index.TokenItemsWithPartial(token, scope, filter, out var volumeFound, cancellationToken));
            found |= volumeFound;
        }
        return results;
    }

    private IReadOnlyCollection<SearchItem> Tokens(string token, TokenMatch mode, int maxDistance, CancellationToken cancellationToken) =>
        volumes.Count == 1
            ? volumes[0].Index.TokenItems(token, mode, maxDistance, volumes[0].Scope, cancellationToken)
            : volumes.SelectMany(volume => volume.Index.TokenItems(token, mode, maxDistance, volume.Scope, cancellationToken)).ToArray();

    public ISearchStateReader WithUpserts(IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer) => throw new NotSupportedException();

    public ISearchStateReader WithoutPathAndDescendants(string path) => throw new NotSupportedException();

    public ISearchStateReader WithChanges(IEnumerable<string> removedPaths, IEnumerable<FileSystemNode> upserts, ITokenizer tokenizer) =>
        throw new NotSupportedException();

    private static bool InScope(string path, IReadOnlyList<string>? scope)
    {
        if (scope is null or { Count: 0 }) return true;
        var full = Path.TrimEndingDirectorySeparator(path);
        foreach (var root in scope)
        {
            var trimmed = Path.TrimEndingDirectorySeparator(root);
            if (full.Equals(trimmed, StringComparison.OrdinalIgnoreCase) ||
                full.StartsWith(trimmed + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    public void Dispose() { }
}
