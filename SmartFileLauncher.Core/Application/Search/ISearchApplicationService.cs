namespace SmartFileLauncher.Core.Application.Search;

public interface ISearchApplicationService
{
    Task<SearchOutcome> SearchAsync(
        SearchRequest request,
        CancellationToken cancellationToken = default);
}
