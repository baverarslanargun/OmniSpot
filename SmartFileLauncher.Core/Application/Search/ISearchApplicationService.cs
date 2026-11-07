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
}
