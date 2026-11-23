using System.IO;
using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.UI.Services;

internal sealed class FolderViewMemory
{
    internal const string RootKey = "";
    internal const int DefaultSortCapacity = 1000;

    private readonly Dictionary<string, ResultView> _views =
        new(StringComparer.OrdinalIgnoreCase);

    private readonly List<FolderSort> _sorts = new();
    private readonly FolderSortStore? _store;
    private readonly int _sortCapacity;

    internal FolderViewMemory(FolderSortStore? store = null, int sortCapacity = DefaultSortCapacity)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(sortCapacity, 1);
        _store = store;
        _sortCapacity = sortCapacity;
        if (store == null)
        {
            return;
        }

        foreach (var saved in store.Load())
        {
            if (saved.Sort == ResultView.FolderDefault.Sort)
            {
                continue;
            }

            var key = Normalize(saved.Path);
            var index = IndexOfSort(key);
            if (index >= 0)
            {
                _sorts.RemoveAt(index);
            }

            _sorts.Add(new FolderSort(key, saved.Sort));
        }

        TrimSorts();
        foreach (var saved in _sorts)
        {
            _views[saved.Path] = ResultView.FolderDefault with { Sort = saved.Sort };
        }
    }

    internal int Count => _views.Count;

    internal int RememberedSortCount => _sorts.Count;

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

        RememberSort(key, view.Sort);
    }

    internal void Clear() => _views.Clear();

    private void RememberSort(string key, ItemSort sort)
    {
        if (_store == null)
        {
            return;
        }

        var index = IndexOfSort(key);
        var isDefault = sort == ResultView.FolderDefault.Sort;
        if (index < 0 ? isDefault : _sorts[index].Sort == sort)
        {
            return;
        }

        if (index >= 0)
        {
            _sorts.RemoveAt(index);
        }

        if (!isDefault)
        {
            _sorts.Add(new FolderSort(key, sort));
            TrimSorts();
        }

        _store.Save(_sorts);
    }

    private int IndexOfSort(string key) =>
        _sorts.FindIndex(saved => string.Equals(saved.Path, key, StringComparison.OrdinalIgnoreCase));

    private void TrimSorts()
    {
        if (_sorts.Count > _sortCapacity)
        {
            _sorts.RemoveRange(0, _sorts.Count - _sortCapacity);
        }
    }

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
