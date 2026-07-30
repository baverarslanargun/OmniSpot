using SmartFileLauncher.Core.Application.Indexing;

namespace SmartFileLauncher.Core.Application.Files;

public sealed class FolderNavigationService : IFolderNavigationService
{
    private readonly IFolderBrowserService _browser;
    private readonly Func<IndexReconciliationStatus> _getReconciliationStatus;
    private readonly Func<string, CancellationToken, Task<bool>> _ensureSynced;

    public FolderNavigationService(
        IFolderBrowserService browser,
        Func<IndexReconciliationStatus> getReconciliationStatus,
        Func<string, CancellationToken, Task<bool>> ensureSynced)
    {
        _browser = browser ?? throw new ArgumentNullException(nameof(browser));
        _getReconciliationStatus = getReconciliationStatus
            ?? throw new ArgumentNullException(nameof(getReconciliationStatus));
        _ensureSynced = ensureSynced
            ?? throw new ArgumentNullException(nameof(ensureSynced));
    }

    public async Task<FolderPage> OpenAsync(
        string folderPath,
        int limit,
        bool ensureSynchronized,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folderPath);
        if (ensureSynchronized &&
            _getReconciliationStatus().IsRunning)
        {
            await _ensureSynced(folderPath, cancellationToken)
                .ConfigureAwait(false);
        }

        return await _browser.LoadAsync(
                folderPath,
                limit,
                cancellationToken)
            .ConfigureAwait(false);
    }

    public string? GetParentWithinRoots(
        string currentPath,
        IReadOnlyList<string> rootPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        ArgumentNullException.ThrowIfNull(rootPaths);
        var current = Normalize(currentPath);
        var roots = rootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .ToArray();

        if (roots.Any(root => PathsEqual(root, current)))
        {
            return null;
        }

        var parent = Path.GetDirectoryName(current);
        if (string.IsNullOrEmpty(parent))
        {
            return null;
        }

        parent = Normalize(parent);
        return roots.Any(root => IsSameOrDescendant(parent, root))
            ? parent
            : null;
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        if (PathsEqual(path, root))
        {
            return true;
        }

        var prefix = root.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        return path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    private static bool PathsEqual(string left, string right)
    {
        return string.Equals(
            left.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            right.TrimEnd(
                Path.DirectorySeparatorChar,
                Path.AltDirectorySeparatorChar),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var fullPath = Path.GetFullPath(path);
        var root = Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) && PathsEqual(fullPath, root))
        {
            return root;
        }

        return fullPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
    }
}
