using System.Buffers;
using System.Text;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

public sealed partial class FileIndex
{
    private static readonly ArrayPool<ulong> LiteralMatchBuffers = ArrayPool<ulong>.Create(maxArrayLength: 1 << 20, maxArraysPerBucket: 1);
    private readonly object _openedRowGate = new();
    private Dictionary<string, (string Path, uint Row)>? _openedRowCache;

    private readonly record struct RankedLiteralRow(uint Row, double Score);

    private readonly record struct OpenedLiteralCandidate(string Path, int NameOffset, byte[]? FoldedName, double Score)
    {
        internal ReadOnlySpan<char> Name => Path.AsSpan(NameOffset);
    }

    private List<OpenedLiteralCandidate>?[] GetOpenedLiteralCandidates(
        string query, byte[] pattern, bool extensionless, LiteralRanking ranking, CancellationToken cancellationToken)
    {
        var candidates = new List<OpenedLiteralCandidate>?[6];
        var asciiQuery = Ascii.IsValid(query);
        foreach (var path in ranking.MatchingOpenedPaths(query))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!path.StartsWith(RootPath, StringComparison.OrdinalIgnoreCase)) continue;
            var boost = ranking.Boost(path);
            if (boost <= 0) continue;
            var nameOffset = path.AsSpan().LastIndexOfAny(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + 1;
            var name = path.AsSpan(nameOffset);
            byte[]? folded = null;
            int tier;
            if (asciiQuery && Ascii.IsValid(name))
            {
                tier = LiteralRanking.Classify(name, query);
            }
            else
            {
                folded = IndexNames.FoldQuery(name.ToString());
                tier = GetLiteralTier(folded, pattern, extensionless);
            }

            if (tier > 0)
                (candidates[tier] ??= []).Add(new OpenedLiteralCandidate(path, nameOffset, folded, tier + boost));
        }
        return candidates;
    }

    private static int CompareOpenedCandidates(OpenedLiteralCandidate left, OpenedLiteralCandidate right)
    {
        var byScore = right.Score.CompareTo(left.Score);
        if (byScore != 0) return byScore;

        var a = left.FoldedName;
        var b = right.FoldedName;
        if (a is null && b is null) return left.Name.CompareTo(right.Name, StringComparison.OrdinalIgnoreCase);
        if (a is not null && b is not null) return IndexNames.CompareIgnoreCase(a, b);
        return a is null ? CompareAsciiToFolded(left.Name, b!) : -CompareAsciiToFolded(right.Name, a);
    }

    private static int CompareAsciiToFolded(ReadOnlySpan<char> ascii, ReadOnlySpan<byte> folded)
    {
        var shared = Math.Min(ascii.Length, folded.Length);
        for (var i = 0; i < shared; i++)
        {
            var order = IndexNames.Upper((byte)ascii[i]).CompareTo(folded[i]);
            if (order != 0) return order;
        }
        return ascii.Length.CompareTo(folded.Length);
    }

    private uint FindOpenedRow(string path)
    {
        lock (_openedRowGate)
        {
            if (_openedRowCache?.TryGetValue(path, out var hit) == true &&
                string.Equals(hit.Path, path, StringComparison.Ordinal)) return hit.Row;
        }

        var row = FindPath(path);
        lock (_openedRowGate)
        {
            var cache = _openedRowCache ??= new(StringComparer.OrdinalIgnoreCase);
            if (cache.Count < LocalOpenHistory.MaxPaths || cache.ContainsKey(path))
                cache[path] = (path, row);
        }
        return row;
    }

    private IReadOnlyList<SearchResult> SearchRankedLiteral(string query, byte[] pattern, int offset, int window,
        ItemFilter filter, HashSet<uint>? scope, LiteralRanking ranking, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (scope is { Count: 0 } || Array.IndexOf(pattern, (byte)0) >= 0) return [];

        var now = DateTime.Now;
        var needsName = filter.Categories != FileFilterCategory.None;
        var extensionless = Array.IndexOf(pattern, (byte)'.') < 0;
        var selected = new List<RankedLiteralRow>(Math.Min(window, 1024));
        var openedRows = new HashSet<uint>();
        var opened = GetOpenedLiteralCandidates(query, pattern, extensionless, ranking, cancellationToken);

        void AppendOpened(int tier)
        {
            if (opened[tier] is not { } candidates) return;
            candidates.Sort(CompareOpenedCandidates);
            var start = selected.Count;
            OpenedLiteralCandidate? cutoff = null;
            foreach (var candidate in candidates)
            {
                if (cutoff is { } bound && CompareOpenedCandidates(bound, candidate) != 0) break;
                cancellationToken.ThrowIfCancellationRequested();
                var row = FindOpenedRow(candidate.Path);
                if (row == NoRow || !IsAlive(row) || openedRows.Contains(row) || !AcceptsMatchedRow(row, filter, needsName, scope, now) ||
                    GetLiteralTier(_names.Scanned(_name[row]), pattern, extensionless) != tier) continue;

                openedRows.Add(row);
                selected.Add(new RankedLiteralRow(row, candidate.Score));
                if (selected.Count == window) cutoff = candidate;
            }

            selected.Sort(start, selected.Count - start, Comparer<RankedLiteralRow>.Create((left, right) =>
            {
                var byScore = right.Score.CompareTo(left.Score);
                return byScore != 0 ? byScore : CompareByName(left.Row, right.Row);
            }));
            if (selected.Count > window) selected.RemoveRange(window, selected.Count - window);
        }

        void AppendRange(int tier, int start, int end)
        {
            AppendOpened(tier);
            var lastName = -1;
            var actualTier = 0;
            for (var i = start; i < end && selected.Count < window; i++)
            {
                if (((i - start) & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var row = _byName.Rows[i];
                var id = _name[row];
                if (id != lastName)
                {
                    lastName = id;
                    actualTier = GetLiteralPrefixTier(_names.Scanned(id), pattern.Length, extensionless);
                }
                if (actualTier == tier && !openedRows.Contains(row) &&
                    AcceptsMatchedRow(row, filter, needsName, scope, now))
                    selected.Add(new RankedLiteralRow(row, tier));
            }
        }

        void AppendMatches(int tier, ReadOnlySpan<ulong> matches)
        {
            AppendOpened(tier);
            if (selected.Count == window) return;

            var order = SparseOrder(matches, cancellationToken) ?? _byName;
            for (var i = 0; i < order.Count && selected.Count < window; i++)
            {
                if ((i & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
                var row = order.Rows[i];
                if (IsMatchedName(matches, _name[row]) && !openedRows.Contains(row) &&
                    AcceptsMatchedRow(row, filter, needsName, scope, now))
                    selected.Add(new RankedLiteralRow(row, tier));
            }
        }

        var prefixStart = LiteralNameBound(_byName, pattern);
        var exactEnd = LiteralNameBound(_byName, pattern, upper: true);
        AppendRange(5, prefixStart, exactEnd);

        if (extensionless && selected.Count < window)
        {
            var stem = new byte[pattern.Length + 1];
            pattern.CopyTo(stem, 0);
            stem[^1] = (byte)'.';
            AppendRange(4, LiteralNameBound(_byName, stem), LiteralNameBound(_byName, stem, upper: true, prefix: true));
        }

        if (selected.Count < window)
        {
            var prefixEnd = LiteralNameBound(_byName, pattern, upper: true, prefix: true);
            AppendRange(3, exactEnd, prefixEnd);
        }

        if (selected.Count < window)
        {
            var words = (_names.Count + 63) >> 6;
            var masks = LiteralMatchBuffers.Rent(words * 2);
            try
            {
                masks.AsSpan(0, words * 2).Clear();
                _names.MatchLiteralRemainder(pattern, masks, words, cancellationToken);
                AppendMatches(2, masks.AsSpan(words, words));
                if (selected.Count < window) AppendMatches(1, masks.AsSpan(0, words));
            }
            finally
            {
                LiteralMatchBuffers.Return(masks);
            }
        }

        cancellationToken.ThrowIfCancellationRequested();
        if (selected.Count <= offset) return [];
        var results = new SearchResult[selected.Count - offset];
        for (var i = 0; i < results.Length; i++)
        {
            if ((i & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            var entry = selected[offset + i];
            results[i] = ToResult(entry.Row, entry.Score);
        }
        return results;
    }

    private int LiteralNameBound(RowOrder order, ReadOnlySpan<byte> target, bool upper = false, bool prefix = false)
    {
        var low = 0;
        var high = order.Count;
        while (low < high)
        {
            var middle = (low + high) >>> 1;
            var name = _names.Scanned(_name[order.Rows[middle]]);
            if (prefix && name.Length > target.Length) name = name[..target.Length];
            var comparison = IndexNames.CompareIgnoreCase(name, target);
            if (comparison < 0 || upper && comparison == 0) low = middle + 1;
            else high = middle;
        }
        return low;
    }

    private static int GetLiteralTier(ReadOnlySpan<byte> name, ReadOnlySpan<byte> pattern, bool extensionless)
    {
        var first = IndexNames.IndexOfIgnoreCase(name, pattern);
        if (first < 0) return 0;
        if (first == 0) return GetLiteralPrefixTier(name, pattern.Length, extensionless);
        return IndexNames.HasLiteralWordStart(name, pattern, first) ? 2 : 1;
    }

    private static int GetLiteralPrefixTier(ReadOnlySpan<byte> name, int patternLength, bool extensionless)
    {
        if (name.Length == patternLength) return 5;
        if (extensionless && name.Length > patternLength + 1 && name.LastIndexOf((byte)'.') == patternLength) return 4;
        return 3;
    }
}
