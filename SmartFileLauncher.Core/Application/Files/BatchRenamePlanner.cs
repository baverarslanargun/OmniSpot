using System.Globalization;

namespace SmartFileLauncher.Core.Application.Files;

public enum BatchRenameCase
{
    Keep,
    Lower,
    Upper,
    Title
}

public sealed record BatchRenameOptions(
    string Find = "",
    string Replace = "",
    string Template = "",
    int StartNumber = 1,
    BatchRenameCase Case = BatchRenameCase.Keep);

public sealed record BatchRenameSource(string Path, bool IsDirectory);

public enum BatchRenameStatus
{
    Unchanged,
    Ready,
    Invalid,
    Duplicate,
    Exists
}

public sealed record BatchRenameItem(
    string SourcePath,
    string OldName,
    string NewName,
    BatchRenameStatus Status);

public static class BatchRenamePlanner
{
    public const string NumberToken = "{n}";
    private static readonly CultureInfo Turkish = CultureInfo.GetCultureInfo("tr-TR");
    private static readonly char[] InvalidNameChars = System.IO.Path.GetInvalidFileNameChars();
    private static readonly HashSet<string> ReservedNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "CON", "PRN", "AUX", "NUL",
        "COM1", "COM2", "COM3", "COM4", "COM5", "COM6", "COM7", "COM8", "COM9",
        "LPT1", "LPT2", "LPT3", "LPT4", "LPT5", "LPT6", "LPT7", "LPT8", "LPT9"
    };

    public static IReadOnlyList<BatchRenameItem> Plan(
        IReadOnlyList<BatchRenameSource> sources,
        BatchRenameOptions options,
        Func<string, bool> exists)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentNullException.ThrowIfNull(options);
        ArgumentNullException.ThrowIfNull(exists);

        var width = Math.Max(1, (options.StartNumber + sources.Count - 1).ToString(CultureInfo.InvariantCulture).Length);
        var planned = new List<BatchRenameItem>(sources.Count);
        for (var index = 0; index < sources.Count; index++)
        {
            var source = sources[index];
            var oldName = System.IO.Path.GetFileName(source.Path.TrimEnd('\\', '/'));
            var newName = Rename(oldName, source.IsDirectory, options, index, width);
            var status = !IsValidName(newName) ? BatchRenameStatus.Invalid
                : string.Equals(newName, oldName, StringComparison.Ordinal) ? BatchRenameStatus.Unchanged
                : !string.Equals(newName, oldName, StringComparison.OrdinalIgnoreCase) &&
                  exists(System.IO.Path.Combine(DirectoryOf(source.Path), newName)) ? BatchRenameStatus.Exists
                : BatchRenameStatus.Ready;
            planned.Add(new BatchRenameItem(source.Path, oldName, newName, status));
        }

        var targets = planned
            .GroupBy(item => (Directory: DirectoryOf(item.SourcePath).ToUpperInvariant(), Name: item.NewName.ToUpperInvariant()))
            .Where(group => group.Count() > 1)
            .SelectMany(group => group)
            .ToHashSet();
        return planned
            .Select(item => item.Status == BatchRenameStatus.Ready && targets.Contains(item)
                ? item with { Status = BatchRenameStatus.Duplicate }
                : item)
            .ToArray();
    }

    public static bool CanApply(IReadOnlyList<BatchRenameItem> plan) =>
        plan.Any(item => item.Status == BatchRenameStatus.Ready) &&
        plan.All(item => item.Status is BatchRenameStatus.Ready or BatchRenameStatus.Unchanged);

    private static string Rename(string oldName, bool isDirectory, BatchRenameOptions options, int index, int width)
    {
        var extension = isDirectory ? string.Empty : System.IO.Path.GetExtension(oldName);
        var stem = oldName[..(oldName.Length - extension.Length)];
        if (!string.IsNullOrWhiteSpace(options.Template))
        {
            var number = (options.StartNumber + index).ToString(CultureInfo.InvariantCulture).PadLeft(width, '0');
            stem = options.Template.Contains(NumberToken, StringComparison.Ordinal)
                ? options.Template.Replace(NumberToken, number, StringComparison.Ordinal)
                : $"{options.Template.TrimEnd()} {number}";
        }

        if (!string.IsNullOrEmpty(options.Find))
        {
            stem = ReplaceIgnoringCase(stem, options.Find, options.Replace ?? string.Empty);
        }

        stem = options.Case switch
        {
            BatchRenameCase.Lower => Turkish.TextInfo.ToLower(stem),
            BatchRenameCase.Upper => Turkish.TextInfo.ToUpper(stem),
            BatchRenameCase.Title => Turkish.TextInfo.ToTitleCase(Turkish.TextInfo.ToLower(stem)),
            _ => stem
        };
        return (stem.Trim() + extension).Trim();
    }

    private static string ReplaceIgnoringCase(string input, string find, string replacement)
    {
        var folded = Fold(input);
        var key = Fold(find);
        var builder = new System.Text.StringBuilder(input.Length);
        var position = 0;
        while (position <= folded.Length)
        {
            var match = folded.IndexOf(key, position, StringComparison.Ordinal);
            if (match < 0) break;
            builder.Append(input, position, match - position).Append(replacement);
            position = match + key.Length;
        }

        return builder.Append(input, position, input.Length - position).ToString();
    }

    private static string Fold(string value) => string.Create(value.Length, value, (buffer, source) =>
    {
        for (var index = 0; index < source.Length; index++)
        {
            buffer[index] = source[index] is 'İ' or 'I' or 'ı' ? 'i' : char.ToLowerInvariant(source[index]);
        }
    });

    private static bool IsValidName(string name) =>
        name.Length > 0 &&
        name.IndexOfAny(InvalidNameChars) < 0 &&
        !name.EndsWith('.') &&
        !name.EndsWith(' ') &&
        !ReservedNames.Contains(System.IO.Path.GetFileNameWithoutExtension(name));

    private static string DirectoryOf(string path) =>
        System.IO.Path.GetDirectoryName(path.TrimEnd('\\', '/')) ?? string.Empty;
}
