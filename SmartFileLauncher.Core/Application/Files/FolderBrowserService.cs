using SmartFileLauncher.Core.IO;

namespace SmartFileLauncher.Core.Application.Files;

public sealed class FolderBrowserService : IFolderBrowserService
{
    private readonly bool _skipReparsePoints;
    private readonly FileSystemPathGuard _pathGuard;

    public FolderBrowserService(bool skipReparsePoints = false)
        : this(skipReparsePoints, FileSystemPathGuard.Default)
    {
    }

    internal FolderBrowserService(
        bool skipReparsePoints,
        FileSystemPathGuard pathGuard)
    {
        _skipReparsePoints = skipReparsePoints;
        _pathGuard = pathGuard ?? throw new ArgumentNullException(nameof(pathGuard));
    }

    public Task<FolderPage> LoadAsync(
        string folderPath,
        int limit,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (limit <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(limit));
        }

        return Task.Run(
            () => Load(folderPath, limit, cancellationToken),
            cancellationToken);
    }

    private FolderPage Load(
        string folderPath,
        int limit,
        CancellationToken cancellationToken)
    {
        if (ShouldSkip(folderPath))
        {
            throw new UnauthorizedAccessException(
                "Ölçüm corpus'u yeniden yönlendirilmiş bir yoldan okunamaz.");
        }

        var result = new List<FolderEntry>();
        var directory = new DirectoryInfo(folderPath);

        foreach (var childDirectory in directory
                     .EnumerateDirectories()
                     .OrderBy(item => item.Name))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= limit)
            {
                break;
            }

            try
            {
                if (ShouldSkip(childDirectory.FullName))
                {
                    continue;
                }

                if ((childDirectory.Attributes & FileAttributes.Hidden) != 0 ||
                    (childDirectory.Attributes & FileAttributes.System) != 0)
                {
                    continue;
                }

                result.Add(new FolderEntry(
                    childDirectory.Name,
                    childDirectory.FullName,
                    true));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        foreach (var file in directory
                     .EnumerateFiles()
                     .OrderBy(item => item.Name))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (result.Count >= limit)
            {
                break;
            }

            try
            {
                if (ShouldSkip(file.FullName))
                {
                    continue;
                }

                if ((file.Attributes & FileAttributes.Hidden) != 0)
                {
                    continue;
                }

                result.Add(new FolderEntry(
                    file.Name,
                    file.FullName,
                    false));
            }
            catch (Exception) when (!cancellationToken.IsCancellationRequested)
            {
            }
        }

        return new FolderPage(result, result.Count >= limit);
    }

    private bool ShouldSkip(string path)
    {
        return _skipReparsePoints &&
               _pathGuard.FindReparsePointInExistingPath(path) != null;
    }
}
