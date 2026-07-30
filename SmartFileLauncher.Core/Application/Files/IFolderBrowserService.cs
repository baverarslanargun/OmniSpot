namespace SmartFileLauncher.Core.Application.Files;

public interface IFolderBrowserService
{
    Task<FolderPage> LoadAsync(
        string folderPath,
        int limit,
        CancellationToken cancellationToken = default);
}
