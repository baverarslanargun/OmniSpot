using System.Text;
using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.Core.Index;

public sealed partial class FileIndex
{
    private const int SuggestionMinimumLength = 4;
    private const int SuggestionCandidates = 8;

    internal (string Query, int Count)? SuggestQuery(string query, ResultView view, IReadOnlyList<string>? scope,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(view);
        if (NameQuery.Create(query) is not { HasOperators: false }) return null;
        var words = NameQuery.Words(query);
        if (words.Count == 0) return null;
        _lock.EnterReadLock();
        try
        {
            var scopeRows = ResolveScope(scope, view.Scope);
            var chosen = new string?[words.Count];
            var missing = new List<int>();
            for (var i = 0; i < words.Count; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var upper = Encoding.UTF8.GetBytes(words[i].ToUpperInvariant());
                var plain = Encoding.UTF8.GetBytes(IndexNames.Plain(words[i]));
                if (_names.Find(upper, plain, false, cancellationToken).AsSpan().ContainsAnyExcept(0UL))
                {
                    chosen[i] = words[i].ToLowerInvariant();
                    continue;
                }
                if (plain.Length < SuggestionMinimumLength) return null;
                missing.Add(i);
            }
            if (missing.Count == 0) return null;

            var names = 0;
            foreach (var i in missing)
            {
                var plain = Encoding.UTF8.GetBytes(IndexNames.Plain(words[i]));
                var maxDistance = plain.Length >= 7 ? 2 : 1;
                var ranked = new List<(string Word, ulong[] Matches, int Names, int Distance, int Whole)>();
                foreach (var candidate in NearWords(plain, maxDistance, cancellationToken, SuggestionCandidates))
                {
                    chosen[i] = Encoding.UTF8.GetString(candidate).ToLowerInvariant();
                    var matches = MatchSuggestion(chosen, cancellationToken);
                    var count = CountNames(matches);
                    if (count > 0) ranked.Add((chosen[i]!, matches, count, IndexWords.Distance(candidate, plain, maxDistance), 0));
                }
                for (var at = 0; at < ranked.Count; at++)
                {
                    var word = Encoding.UTF8.GetBytes(ranked[at].Word.ToUpperInvariant());
                    var whole = _names.Find(word, word, true, cancellationToken);
                    for (var w = 0; w < whole.Length; w++) whole[w] &= ranked[at].Matches[w];
                    ranked[at] = ranked[at] with { Whole = CountNames(whole) };
                }
                double Score((string Word, ulong[] Matches, int Names, int Distance, int Whole) entry) =>
                    entry.Whole * Math.Pow(10, maxDistance - entry.Distance);
                ranked.Sort((left, right) =>
                {
                    var byScore = Score(right).CompareTo(Score(left));
                    if (byScore != 0) return byScore;
                    return left.Distance != right.Distance ? left.Distance.CompareTo(right.Distance) : right.Names.CompareTo(left.Names);
                });
                chosen[i] = null;
                foreach (var (word, matches, count, _, _) in ranked)
                {
                    if (CountAccepted(matches, view.Filter, scopeRows, 1, cancellationToken) == 0) continue;
                    chosen[i] = word;
                    names = count;
                    break;
                }
                if (chosen[i] is null) return null;
            }

            return (string.Join(' ', chosen!), names);
        }
        finally { _lock.ExitReadLock(); }
    }

    private ulong[] MatchSuggestion(string?[] words, CancellationToken cancellationToken) =>
        NameQuery.Create(string.Join(' ', words.Where(word => word is not null)))!.Match(_names, cancellationToken);

    private static int CountNames(ulong[] matches)
    {
        var count = 0;
        foreach (var word in matches) count += System.Numerics.BitOperations.PopCount(word);
        return count;
    }
}
