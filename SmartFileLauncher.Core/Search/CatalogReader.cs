using SmartFileLauncher.Core.Utilities;

namespace SmartFileLauncher.Core.Search;

internal interface IDirectCatalogMatches
{
    IEnumerable<int> MatchingItems(string token, int? distance, CancellationToken ct);
    string CanonicalToken(string token);
}

internal abstract class CatalogReader
{
    internal virtual bool RequiresOwnCheckpoint => false;
    internal abstract int ItemCount { get; }
    internal abstract int TokenCount { get; }
    internal abstract int MissingParentCount { get; }
    internal abstract int PayloadBytes { get; }
    internal abstract bool UsesVarint { get; }
    internal abstract long Generation { get; }
    internal abstract IEnumerable<string> Tokens { get; }
    internal abstract SearchItem GetItem(int id, Dictionary<int, string>? pathCache = null);
    internal abstract SearchItem GetTransientItem(int id, Dictionary<int, string> sharedPrefixPaths);
    internal abstract bool Matches(int id, QueryCatalogFilter filter);
    internal abstract int Find(string path, Dictionary<int, string>? pathCache = null);
    internal abstract IEnumerable<int> Roots();
    internal abstract IEnumerable<int> Posting(string token);
    internal abstract IEnumerable<int> Children(string path, Dictionary<int, string>? pathCache = null);
    internal abstract string[] ItemTokens(int id);
    internal abstract void WriteNew(string path);
    internal virtual bool ContainsToken(string token) => Tokens.Contains(token, StringComparer.OrdinalIgnoreCase);
    internal virtual IEnumerable<string> MatchingTokens(string token, int? distance, CancellationToken ct)
    {
        foreach (var candidate in Tokens)
        {
            ct.ThrowIfCancellationRequested();
            if (distance is int max ? FuzzyMatcher.IsFuzzyMatch(token, candidate, max)
                : candidate.Contains(token, StringComparison.OrdinalIgnoreCase)) yield return candidate;
        }
    }
}
