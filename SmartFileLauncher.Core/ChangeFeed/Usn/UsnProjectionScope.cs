namespace SmartFileLauncher.Core.ChangeFeed.Usn;

internal sealed class UsnProjectionScope : IUsnDirectoryLookup
{
    private readonly UsnDirectoryMap _map;
    private readonly IUsnDirectoryLookup _mapLookup;
    private readonly Dictionary<UsnFileReference, UsnDirectoryEntry> _pendingSets = new();
    private readonly HashSet<UsnFileReference> _pendingRemovals = new();

    public UsnProjectionScope(UsnDirectoryMap map)
    {
        _map = map;
        _mapLookup = map;
    }

    public UsnFileReference RootReference => _map.RootReference;

    public string RootPath => _map.RootPath;

    public bool TryGetEntry(UsnFileReference reference, out UsnDirectoryEntry entry) =>
        _pendingSets.TryGetValue(reference, out entry) ||
        _mapLookup.TryGetEntry(reference, out entry);

    public bool Contains(UsnFileReference reference) =>
        reference == RootReference ||
        _pendingSets.ContainsKey(reference) ||
        _map.Contains(reference);

    public bool TryResolve(UsnFileReference reference, out string path) =>
        UsnPathResolver.TryResolve(this, reference, out path);

    public void Set(UsnFileReference reference, string name, UsnFileReference parentReference)
    {
        if (!UsnDirectoryNames.IsSingleSegment(name))
        {
            throw new ArgumentException(
                $"Dizin adı tek bir ad parçası olmalıdır: {name}",
                nameof(name));
        }

        if (reference.IsNone || reference == RootReference)
        {
            return;
        }

        _pendingSets[reference] = new UsnDirectoryEntry(reference, name, parentReference);
        _pendingRemovals.Remove(reference);
    }

    public void Remove(UsnFileReference reference)
    {
        if (reference.IsNone || reference == RootReference)
        {
            return;
        }

        _pendingRemovals.Add(reference);
    }

    public void Commit()
    {
        foreach (var entry in _pendingSets.Values)
        {
            _map.Set(entry.Reference, entry.Name, entry.ParentReference);
        }

        _map.RemoveSubtrees(_pendingRemovals);

        _pendingSets.Clear();
        _pendingRemovals.Clear();
    }
}
