using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search;

internal interface IQuerySnapshotLease : IDisposable;

internal interface ILiteralNameQuerySnapshot
{
    IReadOnlyList<SearchResult> SearchLiteralNames(
        string query, int limit, ResultView view, CancellationToken cancellationToken);
}

internal interface IStandardQuerySnapshot
{
    IReadOnlyList<SearchResult> SearchStandard(
        string query, int limit, ResultView view, CancellationToken cancellationToken);
}
