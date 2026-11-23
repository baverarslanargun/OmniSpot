using System.Text;

namespace SmartFileLauncher.Core.Search;

public sealed class LiteralRanking
{
    private readonly Dictionary<string, double> _opened;

    public static LiteralRanking Empty { get; } = new([]);

    internal LiteralRanking(IEnumerable<(string FullPath, double Boost)> opened)
    {
        _opened = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var (fullPath, boost) in opened)
            _opened[fullPath] = boost;
    }

    public double Boost(string fullPath) => _opened.GetValueOrDefault(fullPath);

    public IReadOnlyList<string> MatchingOpenedPaths(string query)
    {
        if (_opened.Count == 0 || string.IsNullOrWhiteSpace(query)) return [];
        List<string>? matches = null;
        foreach (var fullPath in _opened.Keys)
        {
            var path = fullPath.AsSpan();
            var name = path[(path.LastIndexOfAny('\\', '/') + 1)..];
            if (name.Contains(query.AsSpan(), StringComparison.OrdinalIgnoreCase))
                (matches ??= []).Add(fullPath);
        }
        if (matches is null) return [];
        return matches;
    }

    public static int Classify(ReadOnlySpan<char> name, ReadOnlySpan<char> query)
    {
        if (query.IsEmpty) return 0;
        var remaining = name;
        var rank = 0;
        while (remaining.IndexOf(query, StringComparison.OrdinalIgnoreCase) is var next && next >= 0)
        {
            var offset = name.Length - remaining.Length + next;
            if (offset == 0)
            {
                if (name.Length == query.Length) return 5;
                if (!query.Contains('.') && IsStem(name.Length, query.Length, name.LastIndexOf('.'))) return 4;
                return 3;
            }
            if (name[offset - 1] is ' ' or '_' or '-' or '.') return 2;
            rank = 1;
            remaining = remaining[(next + 1)..];
        }
        return rank;
    }

    internal static int ClassifyAscii(ReadOnlySpan<byte> name, ReadOnlySpan<byte> query, bool extensionless)
    {
        if (query.IsEmpty) return 0;
        var first = query[0];
        var lower = (byte)(first | 0x20);
        var isLetter = lower is >= (byte)'a' and <= (byte)'z';
        var remaining = name;
        var rank = 0;
        while (remaining.Length >= query.Length)
        {
            var next = isLetter ? remaining.IndexOfAny(lower, (byte)(lower - 32)) : remaining.IndexOf(first);
            if (next < 0 || next > remaining.Length - query.Length) break;
            if (Ascii.EqualsIgnoreCase(remaining.Slice(next, query.Length), query))
            {
                var offset = name.Length - remaining.Length + next;
                if (offset == 0)
                {
                    if (name.Length == query.Length) return 5;
                    if (extensionless && IsStem(name.Length, query.Length, name.LastIndexOf((byte)'.'))) return 4;
                    return 3;
                }
                if (name[offset - 1] is (byte)' ' or (byte)'_' or (byte)'-' or (byte)'.') return 2;
                rank = 1;
            }
            remaining = remaining[(next + 1)..];
        }
        return rank;
    }

    private static bool IsStem(int nameLength, int queryLength, int lastDot) =>
        lastDot == queryLength && lastDot < nameLength - 1;
}
