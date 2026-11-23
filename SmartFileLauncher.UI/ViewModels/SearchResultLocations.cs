using System.IO;

namespace SmartFileLauncher.UI.ViewModels;

internal static class SearchResultLocations
{
    internal static void Apply(IReadOnlyList<SearchResultViewModel> results)
    {
        foreach (var group in results.GroupBy(result => result.Name, StringComparer.OrdinalIgnoreCase))
        {
            var members = group.ToArray();
            if (members.Length == 1)
            {
                members[0].Location = null;
                continue;
            }

            var parents = members.Select(member => ParentSegments(member.FullPath)).ToArray();
            var labels = new string?[members.Length];
            var deepest = parents.Max(segments => segments.Length);
            for (var depth = 1; depth <= deepest && labels.Contains(null); depth++)
            {
                var candidates = parents.Select(segments => Label(segments, depth)).ToArray();
                var counts = candidates.GroupBy(label => label, StringComparer.OrdinalIgnoreCase)
                    .ToDictionary(label => label.Key, label => label.Count(), StringComparer.OrdinalIgnoreCase);
                for (var at = 0; at < members.Length; at++)
                    if (labels[at] == null && counts[candidates[at]] == 1) labels[at] = candidates[at];
            }

            for (var at = 0; at < members.Length; at++)
                members[at].Location = labels[at] ?? Label(parents[at], parents[at].Length);
        }
    }

    private static string[] ParentSegments(string path)
    {
        var parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path));
        if (string.IsNullOrEmpty(parent)) return [];
        return parent.Split(Path.DirectorySeparatorChar, StringSplitOptions.RemoveEmptyEntries);
    }

    private static string Label(string[] segments, int depth)
    {
        if (segments.Length == 0) return "";
        var label = string.Join(Path.DirectorySeparatorChar, segments.Skip(Math.Max(0, segments.Length - depth)));
        return label.EndsWith(':') ? label + Path.DirectorySeparatorChar : label;
    }
}
