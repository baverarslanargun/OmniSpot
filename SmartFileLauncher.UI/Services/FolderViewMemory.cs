using System.IO;
using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.UI.Services;

internal sealed class FolderViewMemory
{
    internal const string RootKey = "";

    private readonly Dictionary<string, ResultView> _views =
        new(StringComparer.OrdinalIgnoreCase);

    internal int Count => _views.Count;

    internal ResultView Get(string? folderPath) =>
        _views.TryGetValue(Normalize(folderPath), out var view)
            ? view
            : ResultView.FolderDefault;

    internal void Set(string? folderPath, ResultView view)
    {
        ArgumentNullException.ThrowIfNull(view);
        var key = Normalize(folderPath);
        if (view == ResultView.FolderDefault)
        {
            _views.Remove(key);
        }
        else
        {
            _views[key] = view;
        }
    }

    internal void Clear() => _views.Clear();

    private static string Normalize(string? folderPath)
    {
        if (string.IsNullOrWhiteSpace(folderPath))
        {
            return RootKey;
        }

        var trimmed = folderPath.TrimEnd(
            Path.DirectorySeparatorChar,
            Path.AltDirectorySeparatorChar);
        return trimmed.Length == 0 ? RootKey : trimmed;
    }
}
