using System.Text;

namespace SmartFileLauncher.Core.Index;

internal sealed class NameQuery
{
    private readonly Term[] _terms;
    private readonly bool _singleLiteralWord;

    internal readonly record struct Term(string Text, bool Exact, bool Excluded)
    {
        internal string Upper { get; } = Text.ToUpperInvariant();
        internal string Plain { get; } = IndexNames.Plain(Text);
        internal byte[] UpperBytes { get; } = Encoding.UTF8.GetBytes(Text.ToUpperInvariant());
        internal byte[] PlainBytes { get; } = Encoding.UTF8.GetBytes(IndexNames.Plain(Text));
    }

    private NameQuery(Term[] terms, bool singleLiteralWord)
    {
        _terms = terms;
        _singleLiteralWord = singleLiteralWord;
        HasOperators = terms.Any(term => term.Exact || term.Excluded);
        LiteralText = HasOperators ? terms.First(term => !term.Excluded).Text : null;
    }

    internal bool HasOperators { get; }

    internal string? LiteralText { get; }

    internal IReadOnlyList<Term> Terms => _terms;

    internal static NameQuery? Create(string query)
    {
        if (string.IsNullOrWhiteSpace(query)) return null;
        var terms = Parse(query);
        if (!terms.Exists(term => !term.Excluded) || !terms.Exists(term => term.Exact || term.Excluded))
        {
            var parts = Words(query);
            terms = parts.Select(part => new Term(part, false, false)).ToList();
            return Build(terms, parts.Count == 1 && parts[0].Length == query.Length);
        }
        return Build(terms, false);
    }

    private static NameQuery? Build(List<Term> terms, bool singleLiteralWord)
    {
        var unique = new List<Term>();
        foreach (var term in terms)
            if (!unique.Exists(known => known.Upper == term.Upper && known.Exact == term.Exact && known.Excluded == term.Excluded))
                unique.Add(term);
        return unique.Count == 0 ? null : new NameQuery([.. unique], singleLiteralWord);
    }

    internal static List<string> Words(string query)
    {
        var parts = Split(query, true);
        return parts.Count == 0 ? Split(query, false) : parts;
    }

    private static bool IsSeparator(char value) =>
        char.IsWhiteSpace(value) || value is '_' or '-' or ',' or '[' or ']' or '(' or ')';

    private static List<Term> Parse(string query)
    {
        var terms = new List<Term>();
        var at = 0;
        while (at < query.Length)
        {
            if (IsSeparator(query[at]))
            {
                at++;
                continue;
            }
            var excluded = false;
            if (query[at] == '!' && at + 1 < query.Length && !IsSeparator(query[at + 1]))
            {
                excluded = true;
                at++;
            }
            if (query[at] == '"')
            {
                var close = query.IndexOf('"', at + 1);
                var end = close < 0 ? query.Length : close;
                var phrase = query[(at + 1)..end];
                if (!string.IsNullOrWhiteSpace(phrase)) terms.Add(new Term(phrase, true, excluded));
                at = close < 0 ? query.Length : close + 1;
                continue;
            }
            var start = at;
            while (at < query.Length && !IsSeparator(query[at]) && query[at] != '"') at++;
            terms.Add(new Term(query[start..at], false, excluded));
        }
        return terms;
    }

    private static List<string> Split(string query, bool atNameSeparators)
    {
        var parts = new List<string>();
        var start = -1;
        for (var i = 0; i <= query.Length; i++)
        {
            var separator = i == query.Length || (atNameSeparators ? IsSeparator(query[i]) : char.IsWhiteSpace(query[i]));
            if (!separator)
            {
                if (start < 0) start = i;
                continue;
            }
            if (start >= 0) parts.Add(query[start..i]);
            start = -1;
        }
        return parts;
    }

    internal ulong[] MatchBeyondLiteral(IndexNames names, CancellationToken cancellationToken) =>
        _singleLiteralWord
            ? names.FindFolded(_terms[0].UpperBytes, _terms[0].PlainBytes, cancellationToken)
            : Match(names, cancellationToken);

    internal ulong[] Match(IndexNames names, CancellationToken cancellationToken)
    {
        ulong[]? all = null;
        foreach (var term in _terms)
        {
            if (term.Excluded) continue;
            var bits = Find(names, term, cancellationToken);
            if (all is null)
            {
                all = bits;
                continue;
            }
            var any = 0UL;
            for (var i = 0; i < all.Length; i++) any |= all[i] &= bits[i];
            if (any == 0) return all;
        }
        foreach (var term in _terms)
        {
            if (!term.Excluded) continue;
            var bits = Find(names, term, cancellationToken);
            for (var i = 0; i < all!.Length; i++) all[i] &= ~bits[i];
        }
        return all!;
    }

    private static ulong[] Find(IndexNames names, Term term, CancellationToken cancellationToken) =>
        term.Exact
            ? names.Match(term.UpperBytes, cancellationToken)
            : names.Find(term.UpperBytes, term.PlainBytes, false, cancellationToken);

    internal bool Matches(string name)
    {
        var upper = name.ToUpperInvariant();
        string? plain = null;
        foreach (var term in _terms)
            if (Contains(term) == term.Excluded) return false;
        return true;

        bool Contains(Term term)
        {
            if (upper.Contains(term.Upper, StringComparison.Ordinal)) return true;
            if (term.Exact || term.Plain.Length == 0) return false;
            if (upper.Contains(term.Plain, StringComparison.Ordinal)) return true;
            plain ??= IndexNames.Plain(name);
            return plain.Contains(term.Plain, StringComparison.Ordinal);
        }
    }
}
