using System.Text;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search.Paged;

internal sealed partial class Catalog
{
    internal IReadOnlyList<SearchResult> SearchLiteralNames(
        string query, int limit, ResultView view, CancellationToken cancellationToken,
        bool excludeRoot = false)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return [];
        var index = Volatile.Read(ref _literalNameIndex);
        return index is null
            ? SearchLiteralNamesLinear(query, limit, view, cancellationToken, excludeRoot)
            : SearchPreparedLiteralNames(index, query, limit, view, cancellationToken, excludeRoot);
    }

    private IReadOnlyList<SearchResult> SearchLiteralNamesLinear(
        string query, int limit, ResultView view, CancellationToken cancellationToken,
        bool excludeRoot)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrWhiteSpace(query) || limit <= 0) return [];
        var ascii = query.All(character => character <= 127);
        var pattern = ascii ? Encoding.ASCII.GetBytes(query) : [];
        for (var i = 0; i < pattern.Length; i++) pattern[i] = LowerAscii(pattern[i]);
        var sort = view.Sort.IsRelevance ? ItemSort.NameAscending : view.Sort;
        var comparer = new HitOrder(this, sort);
        var worst = Comparer<Hit>.Create((a, b) => comparer.Compare(b, a));
        var selected = new PriorityQueue<Hit, Hit>(worst);
        var now = DateTime.Now;
        var visited = 0;
        Span<char> characters = stackalloc char[1024];
        foreach (var id in ActiveIds())
        {
            if ((visited++ & 255) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (excludeRoot && id == RootId) continue;
            var name = NameBytes(id, out var wide);
            var found = ascii && !wide
                ? ContainsAsciiIgnoreCase(name, pattern)
                : MemoryExtensions.Contains(characters[..DecodeName(name, wide, characters)], query.AsSpan(), StringComparison.OrdinalIgnoreCase);
            if (!found || !Matches(id, view.Filter, now)) continue;
            var hit = new Hit(id, 0);
            if (selected.Count < limit) selected.Enqueue(hit, hit);
            else if (comparer.Compare(hit, selected.Peek()) < 0)
            {
                selected.Dequeue();
                selected.Enqueue(hit, hit);
            }
        }
        cancellationToken.ThrowIfCancellationRequested();
        var ordered = selected.UnorderedItems.Select(item => item.Element).ToList();
        ordered.Sort(comparer);
        var results = new List<SearchResult>(ordered.Count);
        foreach (var hit in ordered)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var item = Item(hit.Id);
            results.Add(new SearchResult
            {
                Name = item.Name, FullPath = item.FullPath, Score = 0,
                IsDirectory = item.IsDirectory, SizeBytes = item.SizeBytes,
                LastWriteTime = item.LastWriteTime
            });
        }
        return results;
    }

    private static bool ContainsAsciiIgnoreCase(ReadOnlySpan<byte> name, ReadOnlySpan<byte> pattern)
    {
        if (pattern.Length > name.Length) return false;
        var first = pattern[0];
        var upper = first is >= (byte)'a' and <= (byte)'z' ? (byte)(first - 32) : first;
        while (name.Length >= pattern.Length)
        {
            var candidate = first == upper ? name.IndexOf(first) : name.IndexOfAny(first, upper);
            if (candidate < 0 || candidate > name.Length - pattern.Length) return false;
            var equal = true;
            for (var index = 1; index < pattern.Length; index++)
                if (LowerAscii(name[candidate + index]) != pattern[index])
                {
                    equal = false;
                    break;
                }
            if (equal) return true;
            name = name[(candidate + 1)..];
        }
        return false;
    }

    private static byte LowerAscii(byte value) => value is >= (byte)'A' and <= (byte)'Z'
        ? (byte)(value + 32) : value;
}
