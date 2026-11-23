using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.Core.Search.Paged;

internal sealed class CatalogScope
{
    internal static readonly CatalogScope Everything = new(null, 0, false);
    private static readonly CatalogScope Nothing = new(null, 0, true);

    private readonly Catalog? _catalog;
    private readonly uint _folder;
    private readonly Dictionary<uint, bool>? _known;

    private CatalogScope(Catalog? catalog, uint folder, bool empty)
    {
        _catalog = catalog;
        _folder = folder;
        IsEmpty = empty;
        if (catalog is not null) _known = new() { [folder] = true, [Catalog.RootId] = false, [0] = false };
    }

    internal bool IsEmpty { get; }

    internal static CatalogScope For(Catalog catalog, string? scope)
    {
        if (scope is null) return Everything;
        var root = catalog.Data.RootPath;
        if (PathScope.IsSameOrUnder(root, scope)) return Everything;
        if (!PathScope.Contains(root, scope)) return Nothing;
        var prefix = root.EndsWith('\\') ? root : root + "\\";
        var folder = Catalog.RootId;
        foreach (var name in Path.TrimEndingDirectorySeparator(scope)[prefix.Length..].Split('\\', StringSplitOptions.RemoveEmptyEntries))
        {
            folder = catalog.FindChild(folder, name);
            if (folder == 0) return Nothing;
        }
        return catalog.IsDirectory(folder) ? new CatalogScope(catalog, folder, false) : Nothing;
    }

    internal bool Contains(uint id)
    {
        if (_catalog is null) return !IsEmpty;
        if (id == _folder) return false;
        var current = (uint)_catalog.Row(id).Parent;
        List<uint>? walked = null;
        bool inside;
        while (!_known!.TryGetValue(current, out inside))
        {
            if (walked is { Count: > 1024 })
            {
                inside = false;
                break;
            }
            (walked ??= []).Add(current);
            current = (uint)_catalog.Row(current).Parent;
        }
        if (walked is not null)
            foreach (var folder in walked)
                _known[folder] = inside;
        return inside;
    }
}
