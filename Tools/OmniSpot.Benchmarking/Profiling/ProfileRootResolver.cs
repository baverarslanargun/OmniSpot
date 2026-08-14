using SmartFileLauncher.Core.Application.Indexing;

namespace OmniSpot.Benchmarking.Profiling;

internal static class ProfileRootResolver
{
    internal static IReadOnlyList<ProfileRootRequest> Resolve(
        bool includeOmniSpotRoots,
        IEnumerable<string>? customRoots)
    {
        var requests = new List<ProfileRootRequest>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ordinals = new Dictionary<ProfileRootKind, int>();

        if (includeOmniSpotRoots)
        {
            var locations = new IndexedLocationProvider().Resolve();
            foreach (var root in locations.RootPaths)
            {
                AddRequest(requests, seen, ordinals, Normalize(root),
                    ClassifyOmniSpotRoot(root, locations.DesktopPath));
            }
        }

        foreach (var root in customRoots ?? Array.Empty<string>())
        {
            if (!string.IsNullOrWhiteSpace(root))
            {
                AddRequest(requests, seen, ordinals, Normalize(root), ProfileRootKind.Custom);
            }
        }

        return requests;
    }

    private static void AddRequest(
        ICollection<ProfileRootRequest> requests,
        ISet<string> seen,
        IDictionary<ProfileRootKind, int> ordinals,
        string path,
        ProfileRootKind kind)
    {
        if (!seen.Add(path))
        {
            return;
        }

        ordinals.TryGetValue(kind, out var ordinal);
        ordinal++;
        ordinals[kind] = ordinal;
        requests.Add(new ProfileRootRequest(path, kind, ordinal));
    }

    private static ProfileRootKind ClassifyOmniSpotRoot(string path, string desktopPath)
    {
        var userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var candidates = new (ProfileRootKind Kind, string Path)[]
        {
            (ProfileRootKind.Desktop, desktopPath),
            (ProfileRootKind.Documents, Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments)),
            (ProfileRootKind.Downloads, Path.Combine(userProfile, "Downloads")),
            (ProfileRootKind.Pictures, Environment.GetFolderPath(Environment.SpecialFolder.MyPictures)),
            (ProfileRootKind.Music, Environment.GetFolderPath(Environment.SpecialFolder.MyMusic)),
            (ProfileRootKind.Videos, Environment.GetFolderPath(Environment.SpecialFolder.MyVideos))
        };

        foreach (var candidate in candidates)
        {
            if (!string.IsNullOrWhiteSpace(candidate.Path) &&
                StringComparer.OrdinalIgnoreCase.Equals(Normalize(path), Normalize(candidate.Path)))
            {
                return candidate.Kind;
            }
        }

        return ProfileRootKind.Custom;
    }

    private static string Normalize(string path)
    {
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (
            exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            throw new ArgumentException("Profil kökü geçerli değil.");
        }
    }
}
