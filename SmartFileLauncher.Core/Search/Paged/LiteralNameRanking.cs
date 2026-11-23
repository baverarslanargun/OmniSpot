using System.Text;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search.Paged;

internal sealed partial class Catalog
{
    private IReadOnlyList<SearchResult> SearchPreparedRankedLiteralNames(LiteralNameIndex index, string query,
        int limit, ResultView view, LiteralRanking ranking, CancellationToken cancellationToken, bool excludeRoot)
    {
        var scope = CatalogScope.For(this, view.Scope);
        if (scope.IsEmpty) return [];
        var pattern = UpperLiteralPattern(query);
        var lowerPattern = Array.ConvertAll(pattern, LowerAscii);
        var extensionless = !query.Contains('.');
        var now = DateTime.Now;
        var opened = ranking.MatchingOpenedPaths(query);
        var rootPrefix = Data.RootPath.TrimEnd('\\') + "\\";
        var tiePaths = opened.Count == 0 ? null : new LiteralTiePaths(this);
        var boosted = new HashSet<uint>();
        var selected = new List<Hit>(Math.Min(limit, index.Rows.Length));

        bool AddOpened(int tier)
        {
            if (selected.Count == limit) return true;
            if (tiePaths is null) return false;
            var bounds = new Dictionary<string, (int Start, int End)>(StringComparer.OrdinalIgnoreCase);
            var hits = new List<Hit>();
            Span<char> characters = stackalloc char[1024];
            foreach (var path in opened)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var name = OpenedLiteralBasename(path);
                if (!IsLiteralHistoryInCatalog(path, rootPrefix) || LiteralRanking.Classify(name, query) != tier) continue;
                var boost = ranking.Boost(path);
                if (boost <= 0) continue;

                var basename = name.ToString();
                if (!bounds.TryGetValue(basename, out var range))
                {
                    var basenamePattern = UpperLiteralPattern(basename);
                    range = (LiteralNameBound(index, basename, basenamePattern, upper: false),
                        LiteralNameBound(index, basename, basenamePattern, upper: true));
                    bounds.Add(basename, range);
                }

                var first = LowerLiteralPathBound(index, tiePaths, path, range.Start, range.End);
                for (var position = first; position < range.End; position++)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    var row = index.Rows[position];
                    if (!tiePaths.Of(row.Id).Equals(path, StringComparison.OrdinalIgnoreCase)) break;
                    if ((excludeRoot && row.Id == RootId) ||
                        ClassifyPreparedLiteralName(index, row, query, pattern, lowerPattern, extensionless, characters) != tier ||
                        !Matches(row.Id, view.Filter, now, scope)) continue;
                    hits.Add(new Hit(row.Id, tier + boost));
                }
            }

            hits.Sort(new HitOrder(this, ItemSort.Relevance));
            foreach (var hit in hits)
            {
                if (selected.Count == limit) break;
                if (boosted.Add(hit.Id)) selected.Add(hit);
            }
            return selected.Count == limit;
        }

        bool AddRange(int start, int end, int tier)
        {
            Span<char> characters = stackalloc char[1024];
            for (var position = start; position < end && selected.Count < limit; position++)
            {
                if ((position & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
                var row = index.Rows[position];
                if ((excludeRoot && row.Id == RootId) || boosted.Contains(row.Id)) continue;
                if (ClassifyPreparedLiteralName(index, row, query, pattern, lowerPattern, extensionless, characters) != tier)
                    continue;
                if (Matches(row.Id, view.Filter, now, scope)) selected.Add(new Hit(row.Id, tier));
            }
            return selected.Count == limit;
        }

        var prefixStart = LiteralNameBound(index, query, pattern, upper: false);
        var exactEnd = LiteralNameBound(index, query, pattern, upper: true);
        if (AddOpened(5) || AddRange(prefixStart, exactEnd, 5))
            return MaterializeLiteralHits(selected, cancellationToken);

        if (extensionless)
        {
            if (AddOpened(4)) return MaterializeLiteralHits(selected, cancellationToken);
            var stem = query + ".";
            byte[] stemPattern = pattern.Length == 0 ? [] : [.. pattern, (byte)'.'];
            var stemStart = LiteralNameBound(index, stem, stemPattern, upper: false);
            var stemEnd = LiteralNameBound(index, stem, stemPattern, upper: true, prefix: true);
            if (AddRange(stemStart, stemEnd, 4))
                return MaterializeLiteralHits(selected, cancellationToken);
        }

        var prefixEnd = LiteralNameBound(index, query, pattern, upper: true, prefix: true);
        if (AddOpened(3) || AddRange(exactEnd, prefixEnd, 3) || AddOpened(2))
            return MaterializeLiteralHits(selected, cancellationToken);

        var blocks = LiteralGramCandidates(index, lowerPattern, cancellationToken);
        var contains = new List<Hit>();
        Span<char> decoded = stackalloc char[1024];
        for (var position = 0; position < index.Rows.Length && selected.Count < limit; position++)
        {
            if ((position & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (position >= prefixStart && position < prefixEnd)
            {
                position = prefixEnd - 1;
                continue;
            }
            if (blocks is not null)
            {
                var block = LiteralBlockOf(index, position);
                if (!blocks[block])
                {
                    position = LiteralBlockEnd(index, block) - 1;
                    continue;
                }
            }

            var row = index.Rows[position];
            if ((excludeRoot && row.Id == RootId) || boosted.Contains(row.Id)) continue;
            var tier = ClassifyPreparedLiteralName(index, row, query, pattern, lowerPattern, extensionless, decoded);
            if (tier == 0 || (tier == 1 && contains.Count >= limit - selected.Count) || !Matches(row.Id, view.Filter, now, scope))
                continue;
            if (tier == 2) selected.Add(new Hit(row.Id, tier));
            else if (tier == 1) contains.Add(new Hit(row.Id, tier));
        }

        AddOpened(1);
        selected.AddRange(contains.Where(hit => !boosted.Contains(hit.Id)).Take(limit - selected.Count));
        return MaterializeLiteralHits(selected, cancellationToken);
    }

    private IReadOnlyList<SearchResult> SearchRankedLiteralNamesLinear(string query, int limit, ResultView view,
        LiteralRanking ranking, CancellationToken cancellationToken, bool excludeRoot)
    {
        var scope = CatalogScope.For(this, view.Scope);
        if (scope.IsEmpty) return [];
        var pattern = query.All(character => character <= 127) ? Encoding.ASCII.GetBytes(query) : [];
        for (var at = 0; at < pattern.Length; at++) pattern[at] = LowerAscii(pattern[at]);
        var now = DateTime.Now;
        var openedNames = new HashSet<int>();
        var rootPrefix = Data.RootPath.TrimEnd('\\') + "\\";
        foreach (var path in ranking.MatchingOpenedPaths(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsLiteralHistoryInCatalog(path, rootPrefix))
                openedNames.Add(string.GetHashCode(OpenedLiteralBasename(path), StringComparison.OrdinalIgnoreCase));
        }
        var best = new HitOrder(this, ItemSort.Relevance);
        var worst = Comparer<Hit>.Create((left, right) => best.Compare(right, left));
        var selected = new PriorityQueue<Hit, Hit>(worst);
        void Consider(Hit hit)
        {
            if (selected.Count < limit) selected.Enqueue(hit, hit);
            else selected.EnqueueDequeue(hit, hit);
        }

        var visited = 0;
        var extensionless = !query.Contains('.');
        Span<char> characters = stackalloc char[1024];
        foreach (var id in ActiveIds())
        {
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (excludeRoot && id == RootId) continue;
            var name = NameBytes(id, out var wide);
            var asciiQuery = pattern.Length != 0 && !wide;
            if (asciiQuery && !ContainsAsciiIgnoreCase(name, pattern)) continue;
            var asciiName = asciiQuery && Ascii(name);
            Span<char> decoded = asciiName ? default : characters[..DecodeName(name, wide, characters)];
            var tier = asciiName
                ? LiteralRanking.ClassifyAscii(name, pattern, extensionless)
                : LiteralRanking.Classify(decoded, query);
            if (tier == 0 || !Matches(id, view.Filter, now, scope)) continue;

            var boost = 0d;
            if (openedNames.Count > 0)
            {
                if (asciiName) decoded = characters[..DecodeName(name, wide, characters)];
                if (openedNames.Contains(string.GetHashCode(decoded, StringComparison.OrdinalIgnoreCase)))
                    boost = ranking.Boost(FullPath(id));
            }
            Consider(new Hit(id, tier + boost));
        }

        var ordered = new Hit[selected.Count];
        for (var at = ordered.Length - 1; at >= 0; at--)
        {
            if ((at & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            ordered[at] = selected.Dequeue();
        }

        return MaterializeLiteralHits(ordered, cancellationToken);
    }

    private bool IsLiteralHistoryInCatalog(string path, string rootPrefix) =>
        path.StartsWith(rootPrefix, StringComparison.OrdinalIgnoreCase) ||
        path.Equals(Data.RootPath, StringComparison.OrdinalIgnoreCase);

    private static ReadOnlySpan<char> OpenedLiteralBasename(string path)
    {
        var span = path.AsSpan();
        return span[(span.LastIndexOfAny('\\', '/') + 1)..];
    }

    private static byte[] UpperLiteralPattern(string text)
    {
        if (!text.All(character => character <= 127)) return [];
        var bytes = Encoding.ASCII.GetBytes(text);
        for (var at = 0; at < bytes.Length; at++)
            if (bytes[at] is >= (byte)'a' and <= (byte)'z') bytes[at] -= 32;
        return bytes;
    }

    private static int LowerLiteralPathBound(LiteralNameIndex index, LiteralTiePaths paths, string path, int start, int end)
    {
        while (start < end)
        {
            var middle = start + (end - start) / 2;
            if (string.Compare(paths.Of(index.Rows[middle].Id), path, StringComparison.OrdinalIgnoreCase) < 0)
                start = middle + 1;
            else
                end = middle;
        }
        return start;
    }

    private static int ClassifyPreparedLiteralName(LiteralNameIndex index, LiteralNameRow row, string query,
        byte[] pattern, byte[] lowerPattern, bool extensionless, Span<char> characters)
    {
        var name = index.Name(row);
        if (row.Ascii && pattern.Length != 0)
        {
            return name.IndexOf(pattern) < 0 ? 0 : LiteralRanking.ClassifyAscii(name, pattern, extensionless);
        }
        if (lowerPattern.Length != 0 && !row.Wide && !ContainsAsciiIgnoreCase(name, lowerPattern)) return 0;
        return LiteralRanking.Classify(characters[..DecodeName(name, row.Wide, characters)], query);
    }

    private static int LiteralNameBound(LiteralNameIndex index, string query, byte[] pattern, bool upper, bool prefix = false)
    {
        var lower = 0;
        var higher = index.Rows.Length;
        Span<char> characters = stackalloc char[1024];
        while (lower < higher)
        {
            var middle = lower + (higher - lower) / 2;
            var row = index.Rows[middle];
            var bytes = index.Name(row);
            int comparison;
            if (row.Ascii && pattern.Length != 0)
            {
                var name = prefix ? bytes[..Math.Min(bytes.Length, pattern.Length)] : bytes;
                comparison = name.SequenceCompareTo(pattern);
            }
            else
            {
                ReadOnlySpan<char> name = characters[..DecodeName(bytes, row.Wide, characters)];
                comparison = prefix && name.StartsWith(query, StringComparison.OrdinalIgnoreCase)
                    ? 0 : name.CompareTo(query, StringComparison.OrdinalIgnoreCase);
            }

            if (comparison < 0 || (upper && comparison == 0)) lower = middle + 1;
            else higher = middle;
        }
        return lower;
    }

    private IReadOnlyList<SearchResult> MaterializeLiteralHits(IReadOnlyList<Hit> hits, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var results = new List<SearchResult>(hits.Count);
        foreach (var hit in hits)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = Item(hit.Id);
            results.Add(new SearchResult
            {
                Name = item.Name, FullPath = item.FullPath, Score = hit.Score,
                IsDirectory = item.IsDirectory, SizeBytes = item.SizeBytes, LastWriteTime = item.LastWriteTime
            });
        }
        return results;
    }
}
