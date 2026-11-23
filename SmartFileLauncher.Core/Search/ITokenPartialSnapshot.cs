namespace SmartFileLauncher.Core.Search;

internal interface ITokenPartialSnapshot
{
    IReadOnlyList<(SearchItem Item, bool Exact)> GetWithPartial(string token, CancellationToken cancellationToken = default);
}
