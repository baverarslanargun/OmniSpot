namespace SmartFileLauncher.Core.Application.Files;

public interface IFolderNavigationService
{
    Task<FolderPage> OpenAsync(
        string folderPath,
        int limit,
        bool ensureSynchronized,
        CancellationToken cancellationToken = default);

    string? GetParentWithinRoots(
        string currentPath,
        IReadOnlyList<string> rootPaths);
}
