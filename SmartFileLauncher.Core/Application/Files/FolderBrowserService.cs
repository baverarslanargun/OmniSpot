namespace SmartFileLauncher.Core.Application.Files;

public sealed class FolderBrowserService : IFolderBrowserService
{
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

    private static FolderPage Load(
        string folderPath,
        int limit,
        CancellationToken cancellationToken)
    {
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
}
