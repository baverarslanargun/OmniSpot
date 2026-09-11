using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Filtering;

[Flags]
public enum FileFilterCategory
{
    None = 0,
    Document = 1,
    Image = 2,
    Video = 4,
    Audio = 8,
    Archive = 16,
    Code = 32,
    Application = 64
}

public static class FileFilterCategories
{
    public static readonly IReadOnlyList<FileFilterCategory> All =
    [
        FileFilterCategory.Document,
        FileFilterCategory.Image,
        FileFilterCategory.Video,
        FileFilterCategory.Audio,
        FileFilterCategory.Archive,
        FileFilterCategory.Code,
        FileFilterCategory.Application
    ];

    private static readonly Dictionary<FileFilterCategory, HashSet<string>> _extensions = Build();

    public static bool Matches(string nameOrExtension, FileFilterCategory categories)
    {
        if (categories == FileFilterCategory.None)
        {
            return true;
        }

        var extension = ResolveExtension(nameOrExtension);
        if (extension.Length == 0)
        {
            return false;
        }

        foreach (var category in All)
        {
            if ((categories & category) == 0)
            {
                continue;
            }

            if (_extensions[category].Contains(extension))
            {
                return true;
            }
        }

        return false;
    }

    public static IReadOnlyCollection<string> GetExtensions(FileFilterCategory category) =>
        _extensions.TryGetValue(category, out var extensions)
            ? extensions
            : Array.Empty<string>();

    private static string ResolveExtension(string nameOrExtension)
    {
        if (string.IsNullOrWhiteSpace(nameOrExtension))
        {
            return string.Empty;
        }

        return nameOrExtension.StartsWith('.') && !nameOrExtension.Contains(Path.DirectorySeparatorChar)
            ? nameOrExtension.ToLowerInvariant()
            : Path.GetExtension(nameOrExtension).ToLowerInvariant();
    }

    private static Dictionary<FileFilterCategory, HashSet<string>> Build() => new()
    {
        [FileFilterCategory.Document] = Combine(
            ["document", "spreadsheet", "presentation", "text"],
            []),
        [FileFilterCategory.Image] = Combine(
            ["image"],
            [".heic"]),
        [FileFilterCategory.Video] = Combine(
            ["video"],
            []),
        [FileFilterCategory.Audio] = Combine(
            ["audio"],
            []),
        [FileFilterCategory.Archive] = Combine(
            ["archive"],
            []),
        [FileFilterCategory.Code] = Combine(
            ["code"],
            [".ts", ".php", ".xaml", ".ps1", ".htm", ".yml", ".yaml", ".sql"]),
        [FileFilterCategory.Application] = Combine(
            ["executable"],
            [])
    };

    private static HashSet<string> Combine(string[] mappedTypes, string[] extras)
    {
        var extensions = new HashSet<string>(
            FileTypeMapper.GetExtensionsForTypes(mappedTypes),
            StringComparer.OrdinalIgnoreCase);
        foreach (var extra in extras)
        {
            extensions.Add(extra);
        }

        return extensions;
    }
}
