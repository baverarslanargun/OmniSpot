using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Application.Search;

public interface ISearchApplicationService
{
    Task<SearchOutcome> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default);

    Task<SearchOutcome> RefreshAsync(
        SearchRequest request,
        SearchOutcome previousOutcome,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<SearchResult>> LoadPageAsync(
        SearchRequest request,
        SearchOutcome outcome,
        int offset,
        int limit,
        CancellationToken cancellationToken = default);

    Task<int?> CountAsync(
        SearchRequest request,
        SearchOutcome outcome,
        CancellationToken cancellationToken = default);
}
