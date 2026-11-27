using System.Buffers;
using System.Numerics;
using System.Runtime.InteropServices;
using System.Text;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

internal sealed class AiRowQuery
{
    internal required (string Token, double Weight, bool UseFuzzy)[] Tokens { get; init; }
    internal required QueryCatalogFilter TokenFilter { get; init; }
    internal required QueryCatalogFilter Metadata { get; init; }
    internal required bool IncludeContents { get; init; }
    internal required IReadOnlySet<string>? FolderHints { get; init; }
    internal required IReadOnlySet<string>? RequiredFolders { get; init; }
    internal required string? RequiredName { get; init; }
    internal required IReadOnlyList<(string Lowered, double Weight)> HintScores { get; init; }
    internal required IReadOnlyList<(int Group, string[] Tokens, double Weight)> FolderContext { get; init; }
    internal required double? FolderBonus { get; init; }
    internal required double? FileBonus { get; init; }
    internal required string[] SoftExtensions { get; init; }
    internal required IReadOnlyList<(string Normalized, double Weight)> NameTerms { get; init; }
    internal required KeyValuePair<string, double>[] ContextTokens { get; init; }
    internal required Func<string, string> Normalize { get; init; }
    internal required Func<string, IEnumerable<string>> Tokenize { get; init; }
    internal required IReadOnlySet<string> ConfirmedFolders { get; init; }

    internal bool ScoresFolders => HintScores.Count > 0 || FolderContext.Count > 0;

    internal bool ScoresNames => NameTerms.Count > 0 || ContextTokens.Length > 0;
}

internal sealed class AiTokenMatches(object names, int generation, ulong[][] partial, ulong[][] exact, ulong found)
{
    internal object Names { get; } = names;
    internal int Generation { get; } = generation;
    internal ulong[][] Partial { get; } = partial;
    internal ulong[][] Exact { get; } = exact;
    internal ulong Found { get; } = found;
}

internal sealed class AiRowResults(int capacity)
{
    private readonly PriorityQueue<Entry, Entry> _queue = new(Comparer<Entry>.Create((left, right) => Compare(right, left)));

    internal double Threshold => capacity > 0 && _queue.Count >= capacity ? _queue.Peek().Score : double.NegativeInfinity;

    internal void Offer(SearchResult result) => Offer(new Entry(result));

    internal void Offer(double score, FileIndex index, uint row)
    {
        if (_queue.Count >= capacity && capacity > 0)
        {
            var worst = _queue.Peek();
            if (score < worst.Score) return;
            if (score == worst.Score && index.CompareAiName(row, worst.Name) > 0) return;
        }
        Offer(new Entry(score, index, row));
    }

    private void Offer(Entry entry)
    {
        if (capacity <= 0) return;
        if (_queue.Count < capacity) _queue.Enqueue(entry, entry);
        else if (Compare(entry, _queue.Peek()) < 0) _queue.EnqueueDequeue(entry, entry);
    }

    internal void Seal(FileIndex index)
    {
        foreach (var (entry, _) in _queue.UnorderedItems)
            if (ReferenceEquals(entry.Index, index)) entry.Seal();
    }

    internal SearchResult[] ToArray()
    {
        var results = new SearchResult[_queue.Count];
        for (var i = results.Length - 1; i >= 0; i--) results[i] = _queue.Dequeue().Result!;
        return results;
    }

    private static int Compare(Entry x, Entry y)
    {
        if (ReferenceEquals(x, y)) return 0;
        var byScore = y.Score.CompareTo(x.Score);
        if (byScore != 0) return byScore;
        var byName = string.Compare(x.Name, y.Name, StringComparison.OrdinalIgnoreCase);
        if (byName != 0) return byName;
        var byPath = string.Compare(x.Path, y.Path, StringComparison.OrdinalIgnoreCase);
        return byPath != 0 ? byPath : string.CompareOrdinal(x.Path, y.Path);
    }

    private sealed class Entry
    {
        private string? _name;
        private string? _path;

        internal Entry(SearchResult result)
        {
            Result = result;
            Score = result.Score;
            _name = result.Name;
            _path = result.FullPath;
        }

        internal Entry(double score, FileIndex index, uint row)
        {
            Score = score;
            Index = index;
            Row = row;
        }

        internal double Score { get; }
        internal FileIndex? Index { get; }
        internal uint Row { get; }
        internal SearchResult? Result { get; private set; }
        internal string Name => _name ??= Index!.NameOf(Row);
        internal string Path => _path ??= Index!.PathOf(Row);

        internal void Seal() => Result ??= Index!.AiResult(Row, Name, Path, Score);
    }
}

public sealed partial class FileIndex
{
    private sealed class AiFolder
    {
        internal bool Hinted;
        internal bool Required;
        internal (int Token, double Weight)[] Inherited = [];
        internal bool[] HintFlags = [];
        internal double[] HintAdditions = [];
        internal bool[] ContextFlags = [];
        internal double? ContextAddition;
    }

    internal int CompareAiName(uint row, string other)
    {
        var id = _name[row];
        var bytes = _names.Original(id);
        if (_names.IsWide(id))
            return MemoryMarshal.Cast<byte, char>(bytes).CompareTo(other.AsSpan(), StringComparison.OrdinalIgnoreCase);
        char[]? rented = null;
        Span<char> buffer = bytes.Length <= 256 ? stackalloc char[256] : rented = ArrayPool<char>.Shared.Rent(bytes.Length);
        try
        {
            var written = Encoding.UTF8.GetChars(bytes, buffer);
            return ((ReadOnlySpan<char>)buffer[..written]).CompareTo(other.AsSpan(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            if (rented != null) ArrayPool<char>.Shared.Return(rented);
        }
    }

    internal SearchResult AiResult(uint row, string name, string path, double score)
    {
        var directory = IsDirectory(row);
        return new SearchResult
        {
            Name = name,
            FullPath = path,
            Score = score,
            IsDirectory = directory,
            SizeBytes = directory ? null : _size[row],
            LastWriteTime = ModifiedOf(row)
        };
    }

    internal AiTokenMatches MatchAiTokens(AiRowQuery query, IReadOnlyList<string>? scope, CancellationToken cancellationToken)
    {
        _lock.EnterReadLock();
        try
        {
            var (partial, exact) = FindAiTokens(query.Tokens, cancellationToken);
            var rows = ResolveScope(scope);
            var pending = 0UL;
            for (var i = 0; i < partial.Length; i++)
                if (partial[i].AsSpan().ContainsAnyExcept(0UL)) pending |= 1UL << i;
            var found = 0UL;
            for (var row = 0u; row < _count && pending != 0; row++)
            {
                if ((row & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (row == _root || !IsAlive(row)) continue;
                var name = _name[row];
                var hits = 0UL;
                for (var rest = pending; rest != 0; rest &= rest - 1)
                {
                    var i = BitOperations.TrailingZeroCount(rest);
                    if (Has(partial[i], name)) hits |= 1UL << i;
                }
                if (hits == 0 || !InScope(row, rows)) continue;
                found |= hits;
                pending &= ~hits;
            }
            return new AiTokenMatches(_names, _generation, partial, exact, found);
        }
        finally { _lock.ExitReadLock(); }
    }

    internal void ScoreAiRows(AiRowQuery query, IReadOnlyList<string>? scope, AiTokenMatches matches, ulong fuzzy,
        AiRowResults results, CancellationToken cancellationToken)
    {
        _lock.EnterReadLock();
        byte[]? folderState = null;
        try
        {
            var tokens = query.Tokens;
            var count = tokens.Length;
            var (partial, exact) = ReferenceEquals(matches.Names, _names) && matches.Generation == _generation
                ? (matches.Partial, matches.Exact)
                : FindAiTokens(tokens, cancellationToken);
            var words = (_names.Count + 63) >> 6;
            var near = new ulong[]?[count];
            for (var rest = fuzzy; rest != 0; rest &= rest - 1)
            {
                var i = BitOperations.TrailingZeroCount(rest);
                if (i >= count) break;
                var plain = Encoding.UTF8.GetBytes(IndexNames.Plain(tokens[i].Token));
                var set = new ulong[words];
                foreach (var word in NearWords(plain, tokens[i].Token.Length >= 7 ? 2 : 1, cancellationToken))
                    Or(set, _names.Find(word, word, true, cancellationToken));
                near[i] = set;
            }

            var own = new ulong[words];
            var expandable = new ulong[words];
            var parentNames = new ulong[words];
            for (var i = 0; i < count; i++)
            {
                Or(own, partial[i]);
                Or(parentNames, exact[i]);
                if (near[i] is { } nearSet) Or(own, nearSet);
                if (!query.IncludeContents || tokens[i].Token.Length < 4) continue;
                Or(expandable, partial[i]);
                if (near[i] is { } nearExpand) Or(expandable, nearExpand);
            }
            var hintRaw = query.FolderHints is { } hints ? AnyPartial(hints) : null;
            var requiredRaw = query.RequiredFolders is { } required ? AnyPartial(required) : null;
            var hintScoreNames = query.HintScores.Count > 0 ? AllWhole(query.HintScores.Select(hint => hint.Lowered.Split(' ', StringSplitOptions.RemoveEmptyEntries))) : null;
            var contextNames = query.FolderContext.Count > 0 ? AllWhole(query.FolderContext.Select(term => term.Tokens)) : null;
            var termNames = query.NameTerms.Select(term => TermNames(term.Normalized)).ToArray();
            var contextTokenNames = query.ContextTokens.Select(item => Find(item.Key, true)).ToArray();
            ulong[]? nameScoreNames = null;
            if (query.ScoresNames)
            {
                nameScoreNames = new ulong[words];
                foreach (var set in termNames) Or(nameScoreNames, set);
                foreach (var set in contextTokenNames) Or(nameScoreNames, set);
            }
            var relevant = new ulong[words];
            Or(relevant, expandable);
            if (hintRaw != null) Or(relevant, hintRaw);
            if (requiredRaw != null) Or(relevant, requiredRaw);
            if (hintScoreNames != null) Or(relevant, hintScoreNames);
            if (contextNames != null) Or(relevant, contextNames);

            var rows = ResolveScope(scope);
            var confirmed = new HashSet<uint>();
            foreach (var path in query.ConfirmedFolders)
            {
                var row = FindPath(path);
                if (row != NoRow) confirmed.Add(row);
            }
            var tokenExtensions = Extensions(query.TokenFilter);
            var metadataExtensions = Extensions(query.Metadata);
            var rootDirectory = Path.GetDirectoryName(RootPath + "x") ?? RootPath;
            var rootInfo = CreateRootFolder(query, rootDirectory);
            var rootMask = TokenMask(Path.GetFileName(rootDirectory));
            var folders = new Dictionary<uint, AiFolder>();
            var parentMasks = new Dictionary<int, ulong>();
            var nameScores = new Dictionary<int, double[]>();
            var softIndexes = new Dictionary<int, int>();
            var weights = new List<(int Token, double Weight)>(Math.Max(4, count));
            var chain = new List<uint>();
            folderState = ArrayPool<byte>.Shared.Rent(_count);
            folderState.AsSpan(0, _count).Clear();
            var stateLength = _count;

            for (var row = 0u; row < _count; row++)
            {
                if ((row & 65535) == 0) cancellationToken.ThrowIfCancellationRequested();
                if (row == _root || !IsAlive(row)) continue;
                var name = _name[row];
                var directory = IsDirectory(row);
                var parent = _parent[row];
                var matched = Has(own, name);
                var info = parent == _root || parent == NoRow || !Hot(parent) ? rootInfo : Folder(parent);
                var expands = query.FolderHints != null && info.Hinted || !directory && info.Inherited.Length > 0;
                if (!matched && !expands) continue;
                if (!InScope(row, rows) || !MatchesTokenCandidate(row, query.Metadata, metadataExtensions)) continue;
                if (directory && confirmed.Contains(row)) continue;
                if (query.RequiredFolders != null && !info.Required) continue;
                if (query.RequiredName is { } requiredName && !AdvancedSearchEngine.ContainsRequiredName(NameOf(row), requiredName)) continue;

                weights.Clear();
                if (matched)
                    for (var i = 0; i < count; i++)
                    {
                        var weight = OwnWeight(i, name);
                        if (!double.IsNaN(weight)) weights.Add((i, weight));
                    }
                if (!directory)
                    foreach (var (token, weight) in info.Inherited)
                    {
                        var at = -1;
                        for (var i = 0; i < weights.Count; i++)
                            if (weights[i].Token == token) { at = i; break; }
                        if (at < 0) weights.Add((token, weight));
                        else if (weight > weights[at].Weight) weights[at] = (token, weight);
                    }

                var sum = 0d;
                if (weights.Count > 0)
                {
                    sum = weights[0].Weight * 1;
                    for (var i = 1; i < weights.Count; i++) sum += weights[i].Weight * 1;
                }
                var score = sum * 100;
                if (weights.Count > 0)
                {
                    var mask = parent == _root || parent == NoRow ? rootMask : ParentMask(parent);
                    var bonus = (mask & 1UL << weights[0].Token) != 0 ? weights[0].Weight * 60 : 0;
                    for (var i = 1; i < weights.Count; i++)
                        if ((mask & 1UL << weights[i].Token) != 0) bonus += weights[i].Weight * 60;
                    score += bonus;
                }
                if (query.ScoresFolders)
                {
                    foreach (var addition in info.HintAdditions) score += addition;
                    if (info.ContextAddition is { } context) score += context;
                }
                if (directory && query.FolderBonus is { } folderBonus) score += folderBonus;
                else if (!directory && query.FileBonus is { } fileBonus) score += fileBonus;
                if (!directory && query.SoftExtensions.Length > 0 && SoftIndex(name) is var soft and >= 0)
                    score += Math.Max(10, 35 - soft * 5);
                if (query.ScoresNames)
                {
                    var ceiling = score + NameCeiling(name);
                    if (ceiling + Math.Abs(ceiling) * 1e-9 + 1e-9 < results.Threshold) continue;
                    foreach (var addition in NameScore(name)) score += addition;
                }
                results.Offer(score, this, row);
            }
            results.Seal(this);

            double OwnWeight(int token, int nameId)
            {
                if (Has(partial[token], nameId))
                    return Has(exact[token], nameId) ? tokens[token].Weight : tokens[token].Weight * 0.7;
                return near[token] is { } nearSet && Has(nearSet, nameId) ? tokens[token].Weight * 0.4 : double.NaN;
            }

            bool Hot(uint folder)
            {
                chain.Clear();
                bool hot;
                var at = folder;
                while (true)
                {
                    if (at == NoRow || at == _root || at >= stateLength) { hot = false; break; }
                    var state = folderState![at];
                    if ((state & 1) != 0) { hot = (state & 2) != 0; break; }
                    chain.Add(at);
                    at = _parent[at];
                }
                for (var i = chain.Count - 1; i >= 0; i--)
                {
                    var member = chain[i];
                    if (Has(relevant, _name[member])) hot = true;
                    folderState![member] = (byte)(hot ? 3 : 1);
                }
                return hot;
            }

            AiFolder Folder(uint folder)
            {
                while (folder != NoRow && folder != _root && !Has(relevant, _name[folder])) folder = _parent[folder];
                if (folder == NoRow || folder == _root) return rootInfo;
                if (folders.TryGetValue(folder, out var known)) return known;
                var parentInfo = Folder(_parent[folder]);
                var id = _name[folder];
                string? text = null;
                string Text() => text ??= _names.Text(id);
                var info = new AiFolder
                {
                    Hinted = parentInfo.Hinted || hintRaw != null && Has(hintRaw, id) && query.FolderHints!.Contains(Text()),
                    Required = parentInfo.Required || requiredRaw != null && Has(requiredRaw, id) && query.RequiredFolders!.Contains(Text()),
                    Inherited = parentInfo.Inherited,
                    HintFlags = parentInfo.HintFlags,
                    HintAdditions = parentInfo.HintAdditions,
                    ContextFlags = parentInfo.ContextFlags,
                    ContextAddition = parentInfo.ContextAddition
                };
                if (query.IncludeContents && Has(expandable, id) && InScope(folder, rows) &&
                    MatchesTokenCandidate(folder, query.TokenFilter, tokenExtensions))
                {
                    var own = new List<(int Token, double Weight)>();
                    var expands = false;
                    for (var i = 0; i < count; i++)
                    {
                        var weight = OwnWeight(i, id);
                        if (double.IsNaN(weight)) continue;
                        own.Add((i, weight * 0.65));
                        if (tokens[i].Token.Length >= 4 && weight >= 1) expands = true;
                    }
                    if (expands)
                    {
                        own.AddRange(parentInfo.Inherited);
                        info.Inherited = own.ToArray();
                    }
                }
                if (hintScoreNames != null && Has(hintScoreNames, id))
                {
                    var normalized = query.Normalize(Text());
                    bool[]? flags = null;
                    for (var h = 0; h < query.HintScores.Count; h++)
                    {
                        if (info.HintFlags[h] || !string.Equals(normalized, query.HintScores[h].Lowered, StringComparison.OrdinalIgnoreCase)) continue;
                        flags ??= (bool[])info.HintFlags.Clone();
                        flags[h] = true;
                    }
                    if (flags != null)
                    {
                        info.HintFlags = flags;
                        info.HintAdditions = HintAdditions(flags);
                    }
                }
                if (contextNames != null && Has(contextNames, id))
                {
                    var segment = query.Tokenize(Text()).Select(SearchTextNormalizer.Fold).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    bool[]? flags = null;
                    for (var t = 0; t < query.FolderContext.Count; t++)
                    {
                        var term = query.FolderContext[t];
                        if (info.ContextFlags[t] || term.Tokens.Length == 0 || !term.Tokens.All(segment.Contains)) continue;
                        flags ??= (bool[])info.ContextFlags.Clone();
                        flags[t] = true;
                    }
                    if (flags != null)
                    {
                        info.ContextFlags = flags;
                        info.ContextAddition = ContextAddition(flags);
                    }
                }
                folders[folder] = info;
                return info;
            }

            AiFolder CreateRootFolder(AiRowQuery plan, string directory)
            {
                var segments = directory.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                var hintFlags = new bool[plan.HintScores.Count];
                var normalized = plan.HintScores.Count > 0
                    ? segments.Select(plan.Normalize).ToHashSet(StringComparer.OrdinalIgnoreCase)
                    : [];
                for (var h = 0; h < hintFlags.Length; h++) hintFlags[h] = normalized.Contains(plan.HintScores[h].Lowered);
                var contextFlags = new bool[plan.FolderContext.Count];
                if (contextFlags.Length > 0)
                {
                    var segmentTokens = segments.Select(segment => plan.Tokenize(segment).Select(SearchTextNormalizer.Fold)
                        .ToHashSet(StringComparer.OrdinalIgnoreCase)).ToArray();
                    for (var t = 0; t < contextFlags.Length; t++)
                    {
                        var term = plan.FolderContext[t];
                        contextFlags[t] = term.Tokens.Length > 0 && segmentTokens.Any(segment => term.Tokens.All(segment.Contains));
                    }
                }
                return new AiFolder
                {
                    Hinted = plan.FolderHints != null && segments.Any(plan.FolderHints.Contains),
                    Required = plan.RequiredFolders != null && segments.Any(plan.RequiredFolders.Contains),
                    HintFlags = hintFlags,
                    HintAdditions = HintAdditions(hintFlags),
                    ContextFlags = contextFlags,
                    ContextAddition = contextFlags.Length > 0 ? ContextAddition(contextFlags) : null
                };
            }

            double[] HintAdditions(bool[] flags)
            {
                var additions = new List<double>();
                for (var h = 0; h < flags.Length; h++)
                    if (flags[h]) additions.Add(query.HintScores[h].Weight);
                return additions.ToArray();
            }

            double ContextAddition(bool[] flags) => query.FolderContext
                .Where((_, index) => flags[index])
                .GroupBy(term => term.Group)
                .Sum(group => group.Max(term => term.Weight) * 100);

            ulong TokenMask(string folderName)
            {
                if (folderName.Length == 0) return 0;
                var set = query.Tokenize(folderName).ToHashSet();
                var mask = 0UL;
                for (var i = 0; i < count; i++)
                    if (set.Contains(tokens[i].Token)) mask |= 1UL << i;
                return mask;
            }

            ulong ParentMask(uint folder)
            {
                var id = _name[folder];
                if (!Has(parentNames, id)) return 0;
                if (parentMasks.TryGetValue(id, out var known)) return known;
                return parentMasks[id] = TokenMask(_names.Text(id));
            }

            int SoftIndex(int id)
            {
                if (softIndexes.TryGetValue(id, out var known)) return known;
                var extension = Path.GetExtension(_names.Text(id)).TrimStart('.');
                var index = Array.FindIndex(query.SoftExtensions, value => string.Equals(value, extension, StringComparison.OrdinalIgnoreCase));
                softIndexes[id] = index;
                return index;
            }

            double[] NameScore(int id)
            {
                if (nameScoreNames == null || !Has(nameScoreNames, id)) return [];
                if (nameScores.TryGetValue(id, out var known)) return known;
                var additions = new List<double>();
                var fileName = Path.GetFileNameWithoutExtension(_names.Text(id));
                var normalizedName = query.Normalize(fileName);
                foreach (var (normalizedTerm, weight) in query.NameTerms)
                {
                    if (normalizedName == normalizedTerm) additions.Add(150 * weight);
                    else if (normalizedTerm.Length > 1 && normalizedName.Contains(normalizedTerm)) additions.Add(75 * weight);
                }
                if (query.ContextTokens.Length > 0)
                {
                    var nameTokens = query.Tokenize(fileName).ToHashSet(StringComparer.OrdinalIgnoreCase);
                    additions.Add(query.ContextTokens.Where(item => nameTokens.Contains(item.Key)).Sum(item => item.Value * 30));
                }
                return nameScores[id] = additions.ToArray();
            }

            ulong[] AnyPartial(IEnumerable<string> values)
            {
                var set = new ulong[words];
                foreach (var value in values)
                    if (value.Length > 0) Or(set, Find(value, false));
                return set;
            }

            ulong[] AllWhole(IEnumerable<string[]> groups)
            {
                var set = new ulong[words];
                foreach (var parts in groups)
                {
                    if (parts.Length == 0) continue;
                    var all = (ulong[])Find(parts[0], true).Clone();
                    for (var p = 1; p < parts.Length; p++) And(all, Find(parts[p], true));
                    Or(set, all);
                }
                return set;
            }

            ulong[] TermNames(string normalized)
            {
                var parts = normalized.Split(' ', StringSplitOptions.RemoveEmptyEntries);
                if (parts.Length == 0) return [];
                var all = (ulong[])Find(parts[0], false).Clone();
                for (var p = 1; p < parts.Length; p++) And(all, Find(parts[p], false));
                return all;
            }

            double NameCeiling(int id)
            {
                var terms = 0d;
                for (var t = 0; t < termNames.Length; t++)
                    if (Has(termNames[t], id)) terms += Math.Max(0, 150 * query.NameTerms[t].Weight);
                var context = 0d;
                for (var k = 0; k < contextTokenNames.Length; k++)
                    if (Has(contextTokenNames[k], id)) context += Math.Max(0, query.ContextTokens[k].Value * 30);
                return terms + context;
            }

            ulong[] Find(string value, bool wholeWord) => _names.Find(Encoding.UTF8.GetBytes(value.ToUpperInvariant()),
                Encoding.UTF8.GetBytes(IndexNames.Plain(value)), wholeWord, cancellationToken);
        }
        finally
        {
            if (folderState != null) ArrayPool<byte>.Shared.Return(folderState);
            _lock.ExitReadLock();
        }
    }

    private (ulong[][] Partial, ulong[][] Exact) FindAiTokens((string Token, double Weight, bool UseFuzzy)[] tokens,
        CancellationToken cancellationToken)
    {
        var partial = new ulong[tokens.Length][];
        var exact = new ulong[tokens.Length][];
        for (var i = 0; i < tokens.Length; i++)
        {
            var upper = Encoding.UTF8.GetBytes(tokens[i].Token.ToUpperInvariant());
            var plain = Encoding.UTF8.GetBytes(IndexNames.Plain(tokens[i].Token));
            partial[i] = _names.Find(upper, plain, false, cancellationToken);
            exact[i] = _names.Find(upper, plain, true, cancellationToken);
        }
        return (partial, exact);
    }

    private static string[]? Extensions(QueryCatalogFilter filter) =>
        filter.AllowedExtensions is { Count: > 0 } allowed ? allowed.ToArray() : null;

    private static bool Has(ulong[] bits, int id) =>
        (uint)(id >> 6) < (uint)bits.Length && (bits[id >> 6] & 1UL << (id & 63)) != 0;

    private static void Or(ulong[] target, ulong[] source)
    {
        var length = Math.Min(target.Length, source.Length);
        for (var i = 0; i < length; i++) target[i] |= source[i];
    }

    private static void And(ulong[] target, ulong[] source)
    {
        var length = Math.Min(target.Length, source.Length);
        for (var i = 0; i < length; i++) target[i] &= source[i];
        for (var i = length; i < target.Length; i++) target[i] = 0;
    }
}
