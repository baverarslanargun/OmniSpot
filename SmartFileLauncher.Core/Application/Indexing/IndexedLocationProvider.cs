namespace SmartFileLauncher.Core.Application.Indexing;

public sealed class IndexedLocationProvider : IIndexedLocationProvider
{
    private readonly Func<Environment.SpecialFolder, string> _getFolderPath;
    private readonly Func<string, bool> _directoryExists;

    public IndexedLocationProvider()
        : this(Environment.GetFolderPath, Directory.Exists)
    {
    }

    public IndexedLocationProvider(
        Func<Environment.SpecialFolder, string> getFolderPath,
        Func<string, bool> directoryExists)
    {
        _getFolderPath = getFolderPath ?? throw new ArgumentNullException(nameof(getFolderPath));
        _directoryExists = directoryExists ?? throw new ArgumentNullException(nameof(directoryExists));
    }

    public IndexLocations Resolve()
    {
        var userProfile = _getFolderPath(Environment.SpecialFolder.UserProfile);
        var desktop = ResolveDesktopPath(userProfile);
        var roots = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        AddIfAvailable(roots, seen, desktop);
        AddIfAvailable(roots, seen, _getFolderPath(Environment.SpecialFolder.MyDocuments));
        AddIfAvailable(roots, seen, Path.Combine(userProfile, "Downloads"));
        AddIfAvailable(roots, seen, _getFolderPath(Environment.SpecialFolder.MyPictures));
        AddIfAvailable(roots, seen, _getFolderPath(Environment.SpecialFolder.MyMusic));
        AddIfAvailable(roots, seen, _getFolderPath(Environment.SpecialFolder.MyVideos));

        return new IndexLocations(desktop, roots);
    }

    private string ResolveDesktopPath(string userProfile)
    {
        var desktop = _getFolderPath(Environment.SpecialFolder.Desktop);
        if (!string.IsNullOrWhiteSpace(desktop) && _directoryExists(desktop))
        {
            return desktop;
        }

        var candidates = new[]
        {
            Path.Combine(userProfile, "OneDrive", "Masaüstü"),
            Path.Combine(userProfile, "OneDrive", "Desktop"),
            Path.Combine(userProfile, "Desktop")
        };

        return candidates.FirstOrDefault(_directoryExists) ?? candidates[^1];
    }

    private void AddIfAvailable(
        ICollection<string> roots,
        ISet<string> seen,
        string path)
    {
        if (string.IsNullOrWhiteSpace(path) ||
            !_directoryExists(path) ||
            !seen.Add(path))
        {
            return;
        }

        roots.Add(path);
    }
}
