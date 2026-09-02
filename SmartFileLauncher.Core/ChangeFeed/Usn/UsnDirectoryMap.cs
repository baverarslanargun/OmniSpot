using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public readonly record struct UsnDirectoryEntry(
    UsnFileReference Reference,
    string Name,
    UsnFileReference ParentReference);

internal interface IUsnDirectoryLookup
{
    UsnFileReference RootReference { get; }

    string RootPath { get; }

    bool TryGetEntry(UsnFileReference reference, out UsnDirectoryEntry entry);
}

public sealed class UsnDirectoryMap : IUsnDirectoryLookup
{
    internal const int MaximumDepth = 512;

    private readonly Dictionary<UsnFileReference, UsnDirectoryEntry> _entries = new();

    public UsnDirectoryMap(string rootPath, UsnFileReference rootReference)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Kök yolu boş olamaz.", nameof(rootPath));
        }

        if (rootReference.IsNone)
        {
            throw new ArgumentException("Kök kimliği boş olamaz.", nameof(rootReference));
        }

        RootPath = Path.TrimEndingDirectorySeparator(rootPath);
        RootReference = rootReference;
    }

    public string RootPath { get; }

    public UsnFileReference RootReference { get; }

    public int Count => _entries.Count;

    public IReadOnlyCollection<UsnDirectoryEntry> Entries => _entries.Values;

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

        _entries[reference] = new UsnDirectoryEntry(reference, name, parentReference);
    }

    public bool Remove(UsnFileReference reference) => _entries.Remove(reference);

    public int RemoveSubtrees(IReadOnlyCollection<UsnFileReference> references)
    {
        if (references.Count == 0 || _entries.Count == 0)
        {
            return 0;
        }

        var childrenByParent = new Dictionary<UsnFileReference, List<UsnFileReference>>();
        foreach (var entry in _entries.Values)
        {
            if (!childrenByParent.TryGetValue(entry.ParentReference, out var children))
            {
                children = new List<UsnFileReference>();
                childrenByParent[entry.ParentReference] = children;
            }

            children.Add(entry.Reference);
        }

        var removed = 0;
        var visited = new HashSet<UsnFileReference>();
        var pending = new Stack<UsnFileReference>(references);

        while (pending.Count > 0)
        {
            var current = pending.Pop();
            if (!visited.Add(current))
            {
                continue;
            }

            if (_entries.Remove(current))
            {
                removed++;
            }

            if (childrenByParent.TryGetValue(current, out var children))
            {
                foreach (var child in children)
                {
                    pending.Push(child);
                }
            }
        }

        return removed;
    }

    public bool Contains(UsnFileReference reference) =>
        reference == RootReference || _entries.ContainsKey(reference);

    public bool TryResolve(UsnFileReference reference, out string path) =>
        UsnPathResolver.TryResolve(this, reference, out path);

    bool IUsnDirectoryLookup.TryGetEntry(UsnFileReference reference, out UsnDirectoryEntry entry) =>
        _entries.TryGetValue(reference, out entry);
}

internal static class UsnDirectoryNames
{
    private static readonly char[] Separators =
    {
        Path.DirectorySeparatorChar,
        Path.AltDirectorySeparatorChar,
        Path.VolumeSeparatorChar
    };

    public static bool IsSingleSegment(string? name)
    {
        if (string.IsNullOrWhiteSpace(name))
        {
            return false;
        }

        if (name is "." or "..")
        {
            return false;
        }

        return name.AsSpan().IndexOfAny(Separators) < 0;
    }
}

internal static class UsnRootScope
{
    public static bool TryCanonicalize(string rootPath, string candidate, out string canonical)
    {
        canonical = string.Empty;

        if (string.IsNullOrWhiteSpace(candidate) || string.IsNullOrWhiteSpace(rootPath))
        {
            return false;
        }

        string resolved;
        try
        {
            resolved = Path.TrimEndingDirectorySeparator(Path.GetFullPath(candidate));
        }
        catch (Exception failure)
            when (failure is ArgumentException or NotSupportedException or PathTooLongException)
        {
            return false;
        }

        var root = Path.TrimEndingDirectorySeparator(rootPath);

        if (string.Equals(resolved, root, StringComparison.OrdinalIgnoreCase))
        {
            canonical = resolved;
            return true;
        }

        if (!resolved.StartsWith(root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        canonical = resolved;
        return true;
    }
}

internal static class UsnPathResolver
{
    public static bool TryResolve(
        IUsnDirectoryLookup lookup,
        UsnFileReference reference,
        out string path)
    {
        if (reference == lookup.RootReference)
        {
            path = lookup.RootPath;
            return true;
        }

        var segments = new List<string>();
        var current = reference;

        for (var depth = 0; depth < UsnDirectoryMap.MaximumDepth; depth++)
        {
            if (!lookup.TryGetEntry(current, out var entry))
            {
                path = string.Empty;
                return false;
            }

            segments.Add(entry.Name);
            current = entry.ParentReference;

            if (current == lookup.RootReference)
            {
                segments.Reverse();
                path = Path.Combine(
                    new[] { lookup.RootPath }.Concat(segments).ToArray());
                return true;
            }
        }

        path = string.Empty;
        return false;
    }
}
