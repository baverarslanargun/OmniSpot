namespace SmartFileLauncher.Core.Application.Files;

public sealed record FolderPathSegment(string Name, string Path);

public sealed record FolderPathLayout(
    IReadOnlyList<FolderPathSegment> Leading,
    IReadOnlyList<FolderPathSegment> Hidden,
    IReadOnlyList<FolderPathSegment> Trailing);

public static class FolderPathTrail
{
    private const int SegmentOverhead = 4;
    private const int MaxSegmentCost = 24;
    private static readonly char[] Separators =
    [
        System.IO.Path.DirectorySeparatorChar,
        System.IO.Path.AltDirectorySeparatorChar
    ];

    public static IReadOnlyList<FolderPathSegment> Build(
        string currentPath,
        IReadOnlyList<string> rootPaths)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(currentPath);
        ArgumentNullException.ThrowIfNull(rootPaths);
        var current = Normalize(currentPath);
        var matchedRoot = rootPaths
            .Where(path => !string.IsNullOrWhiteSpace(path))
            .Select(Normalize)
            .Where(path => IsSameOrDescendant(current, path))
            .OrderByDescending(path => path.Length)
            .FirstOrDefault();
        var root = matchedRoot == null
            ? System.IO.Path.GetPathRoot(current) ?? current
            : current[..matchedRoot.Length];

        var segments = new List<FolderPathSegment> { new(DisplayName(root), root) };
        var path = root;
        foreach (var part in current[Math.Min(root.Length, current.Length)..]
                     .Split(Separators, StringSplitOptions.RemoveEmptyEntries))
        {
            path = System.IO.Path.Combine(path, part);
            segments.Add(new FolderPathSegment(part, path));
        }

        return segments;
    }

    public static FolderPathLayout Fit(
        IReadOnlyList<FolderPathSegment> segments,
        int budget)
    {
        ArgumentNullException.ThrowIfNull(segments);
        if (segments.Count <= 2 || segments.Sum(Cost) <= budget)
        {
            return new FolderPathLayout(segments, [], []);
        }

        var remaining = budget - Cost(segments[0]) - (1 + SegmentOverhead);
        var trailing = new List<FolderPathSegment>();
        for (var index = segments.Count - 1; index >= 1; index--)
        {
            var cost = Cost(segments[index]);
            if (trailing.Count > 0 && cost > remaining)
            {
                break;
            }

            trailing.Insert(0, segments[index]);
            remaining -= cost;
        }

        var hiddenCount = segments.Count - 1 - trailing.Count;
        return hiddenCount <= 0
            ? new FolderPathLayout(segments, [], [])
            : new FolderPathLayout(
                [segments[0]],
                segments.Skip(1).Take(hiddenCount).ToArray(),
                trailing);
    }

    private static int Cost(FolderPathSegment segment) =>
        Math.Min(segment.Name.Length, MaxSegmentCost) + SegmentOverhead;

    private static string DisplayName(string root)
    {
        var trimmed = root.TrimEnd(Separators);
        var name = System.IO.Path.GetFileName(trimmed);
        return string.IsNullOrEmpty(name) ? trimmed : name;
    }

    private static bool IsSameOrDescendant(string path, string root)
    {
        var trimmedRoot = root.TrimEnd(Separators);
        var trimmedPath = path.TrimEnd(Separators);
        return string.Equals(trimmedPath, trimmedRoot, StringComparison.OrdinalIgnoreCase)
            || trimmedPath.StartsWith(
                trimmedRoot + System.IO.Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase);
    }

    private static string Normalize(string path)
    {
        var fullPath = System.IO.Path.GetFullPath(path);
        var root = System.IO.Path.GetPathRoot(fullPath);
        if (!string.IsNullOrEmpty(root) &&
            string.Equals(
                fullPath.TrimEnd(Separators),
                root.TrimEnd(Separators),
                StringComparison.OrdinalIgnoreCase))
        {
            return root;
        }

        return fullPath.TrimEnd(Separators);
    }
}
