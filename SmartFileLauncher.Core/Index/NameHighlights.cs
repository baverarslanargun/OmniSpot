using System.Globalization;
using System.Text;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Index;

public static class NameHighlights
{
    public static IReadOnlyList<(int Start, int Length)> Find(string name, string? query)
    {
        if (string.IsNullOrEmpty(name) || NameQuery.Create(query ?? "") is not { } parsed) return [];
        var upper = name.ToUpperInvariant();
        string? folded = null;
        List<int>? map = null;
        var marked = new bool[name.Length];
        foreach (var term in parsed.Terms)
        {
            if (term.Excluded) continue;
            Mark(upper, term.Upper, null);
            if (term.Exact || term.Plain.Length == 0) continue;
            if (folded is null) (folded, map) = Fold(name);
            Mark(folded, term.Plain, map);
        }

        var ranges = new List<(int Start, int Length)>();
        for (var i = 0; i < marked.Length;)
        {
            if (!marked[i])
            {
                i++;
                continue;
            }
            var start = i;
            while (i < marked.Length && marked[i]) i++;
            ranges.Add((start, i - start));
        }
        return ranges;

        void Mark(string text, string pattern, List<int>? positions)
        {
            if (pattern.Length == 0) return;
            for (var at = text.IndexOf(pattern, StringComparison.Ordinal); at >= 0;
                 at = at + 1 < text.Length ? text.IndexOf(pattern, at + 1, StringComparison.Ordinal) : -1)
            {
                var first = positions?[at] ?? at;
                var last = positions?[at + pattern.Length - 1] ?? at + pattern.Length - 1;
                while (last + 1 < name.Length && (char.IsLowSurrogate(name[last + 1]) || IsMark(name[last + 1]))) last++;
                for (var i = first; i <= last; i++) marked[i] = true;
            }
        }
    }

    private static (string Text, List<int> Map) Fold(string name)
    {
        var builder = new StringBuilder(name.Length);
        var map = new List<int>(name.Length);
        for (var i = 0; i < name.Length; i++)
        {
            var character = name[i];
            if (character < 128)
            {
                builder.Append(char.ToUpperInvariant(character));
                map.Add(i);
                continue;
            }
            foreach (var part in SearchTextNormalizer.Fold(character.ToString()).ToUpperInvariant())
            {
                builder.Append(part);
                map.Add(i);
            }
        }
        return (builder.ToString(), map);
    }

    private static bool IsMark(char character) => CharUnicodeInfo.GetUnicodeCategory(character) is
        UnicodeCategory.NonSpacingMark or UnicodeCategory.SpacingCombiningMark or UnicodeCategory.EnclosingMark;
}
