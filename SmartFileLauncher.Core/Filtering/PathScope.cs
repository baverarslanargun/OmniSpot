namespace SmartFileLauncher.Core.Filtering;

public static class PathScope
{
    public static string? Normalize(string? path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        try
        {
            return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        }
        catch (Exception exception) when (exception is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return null;
        }
    }

    public static bool Contains(string? scope, string path)
    {
        if (scope is null) return true;
        if (string.IsNullOrEmpty(path)) return false;
        var prefix = Prefix(scope);
        var full = Path.TrimEndingDirectorySeparator(path);
        return full.Length > prefix.Length && full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase);
    }

    public static bool IsSameOrUnder(string path, string ancestor) =>
        Path.TrimEndingDirectorySeparator(path).Equals(Path.TrimEndingDirectorySeparator(ancestor), StringComparison.OrdinalIgnoreCase) ||
        Contains(Path.TrimEndingDirectorySeparator(ancestor), path);

    private static string Prefix(string scope)
    {
        var trimmed = Path.TrimEndingDirectorySeparator(scope);
        return trimmed.EndsWith(Path.DirectorySeparatorChar) ? trimmed : trimmed + Path.DirectorySeparatorChar;
    }
}
