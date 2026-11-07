using System.Globalization;
using SmartFileLauncher.Core.ChangeFeed.Store;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

public sealed record IndexInventoryEntry(
    string Path,
    bool IsDirectory,
    FileAttributes Attributes,
    long SizeBytes,
    long CreatedTimeUtc,
    long LastWriteTimeUtc,
    NtfsInventoryIdentity? Identity = null);

public sealed record NtfsInventoryIdentity(
    string VolumeId, ulong FileReference, ulong ParentFileReference, string Name);

public sealed partial class NtfsRootInventory
{
    private readonly Func<string, CancellationToken, IEnumerable<NtfsMftEntry>> _read;

    public NtfsRootInventory() : this(ReadVolume) { }

    internal NtfsRootInventory(Func<string, CancellationToken, IEnumerable<NtfsMftEntry>> read) =>
        _read = read;

    public IEnumerable<IndexInventoryEntry> Read(
        IReadOnlyList<ChangeFeedSubscribedRoot> roots,
        CancellationToken cancellationToken) => Read(roots, cancellationToken, null);

    internal IEnumerable<IndexInventoryEntry> Read(
        IReadOnlyList<ChangeFeedSubscribedRoot> roots,
        CancellationToken cancellationToken, NtfsInventorySecurityGuard? security)
    {
        foreach (var group in roots.GroupBy(root => Path.GetPathRoot(root.RootPath)!,
                     StringComparer.OrdinalIgnoreCase))
        {
            security?.BeginVolume(group.Key);
            var volumeId = group.First().Identity.VolumeId;
            if (group.Any(root => root.Identity.VolumeId != volumeId))
                throw new InvalidDataException("Aynı diskte farklı birim kimlikleri var.");
            var selected = new Dictionary<ulong, string>();
            foreach (var root in group)
            {
                if (!root.Identity.NodeId.StartsWith("0x", StringComparison.Ordinal) ||
                    !ulong.TryParse(root.Identity.NodeId.AsSpan(2), NumberStyles.HexNumber,
                        CultureInfo.InvariantCulture, out var reference))
                    throw new NotSupportedException("NTFS kök kimliği desteklenmiyor.");
                selected.Add(reference, Path.TrimEndingDirectorySeparator(root.RootPath));
            }

            var directories = new Dictionary<ulong, NtfsMftEntry>();
            foreach (var entry in _read(group.Key, cancellationToken))
            {
                if (!entry.IsDirectory) continue;
                if (directories.TryGetValue(entry.FileReference, out var existing) &&
                    (existing.Name != entry.Name || existing.ParentFileReference != entry.ParentFileReference))
                    throw new NotSupportedException("Birden fazla dizin adı desteklenmiyor.");
                directories[entry.FileReference] = entry;
            }
            if (selected.Keys.Any(reference => !directories.ContainsKey(reference)))
                throw new InvalidDataException("MFT kök dizini bulunamadı.");

            var protectedDirectories = selected.Keys.ToHashSet();
            if (security is not null)
            {
                foreach (var root in selected.Keys)
                {
                    var reference = root;
                    for (var depth = 0; ; depth++)
                    {
                        if (depth >= 512 || !directories.TryGetValue(reference, out var ancestor))
                            throw new InvalidDataException("MFT kök üst dizin zinciri tutarsız.");
                        security.Include(group.Key, reference);
                        protectedDirectories.Add(reference);
                        if ((reference & 0xFFFFFFFFFFFFUL) == 5) break;
                        reference = ancestor.ParentFileReference;
                    }
                }
            }

            var resolved = new Dictionary<ulong, string?>();
            void ExpectDirectoryChange(ulong reference)
            {
                if (security is null || protectedDirectories.Contains(reference))
                    throw new InvalidDataException("MFT dizinleri okuma sırasında değişti.");
                security.ExpectDirectoryChange(group.Key, reference);
            }

            string? Resolve(ulong reference, int depth = 0)
            {
                if (selected.TryGetValue(reference, out var rootPath)) return rootPath;
                if (resolved.TryGetValue(reference, out var cached)) return cached;
                if (depth >= 512)
                    throw new InvalidDataException("MFT dizin zinciri tutarsız.");
                if (!directories.TryGetValue(reference, out var directory))
                {
                    ExpectDirectoryChange(reference);
                    return resolved[reference] = null;
                }
                if ((reference & 0xFFFFFFFFFFFFUL) == 5 ||
                    (directory.Attributes & FileAttributes.ReparsePoint) != 0)
                    return resolved[reference] = null;
                var parent = Resolve(directory.ParentFileReference, depth + 1);
                return resolved[reference] = parent is null ? null : Join(parent, directory.Name);
            }

            foreach (var entry in directories.Values)
            {
                cancellationToken.ThrowIfCancellationRequested();
                string? path;
                if (selected.TryGetValue(entry.FileReference, out var rootPath))
                {
                    path = rootPath;
                }
                else
                {
                    var parent = Resolve(entry.ParentFileReference);
                    path = parent is null ? null : Join(parent, entry.Name);
                }
                if (path is not null)
                {
                    security?.Include(group.Key, entry.FileReference);
                    yield return new IndexInventoryEntry(path, entry.IsDirectory, entry.Attributes,
                        entry.SizeBytes, entry.CreatedTimeUtc, entry.LastWriteTimeUtc,
                        new NtfsInventoryIdentity(volumeId, entry.FileReference, entry.ParentFileReference, entry.Name));
                }
            }

            var remainingDirectories = directories.Keys.ToHashSet();
            foreach (var entry in _read(group.Key, cancellationToken))
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    remainingDirectories.Remove(entry.FileReference);
                    if (!directories.TryGetValue(entry.FileReference, out var old) ||
                        old.Name != entry.Name || old.ParentFileReference != entry.ParentFileReference)
                        ExpectDirectoryChange(entry.FileReference);
                    continue;
                }
                var parent = Resolve(entry.ParentFileReference);
                if (parent is null) continue;
                security?.Include(group.Key, entry.FileReference);
                yield return new IndexInventoryEntry(Join(parent, entry.Name), false, entry.Attributes,
                    entry.SizeBytes, entry.CreatedTimeUtc, entry.LastWriteTimeUtc,
                    new NtfsInventoryIdentity(volumeId, entry.FileReference, entry.ParentFileReference, entry.Name));
            }
            foreach (var reference in remainingDirectories) ExpectDirectoryChange(reference);
        }
    }

    private static string Join(string parent, string name)
    {
        if (name.Length == 0 || name is "." or ".." || name.IndexOfAny(['\\', '/', ':', '\0']) >= 0)
            throw new InvalidDataException("MFT dosya adı geçersiz.");
        return Path.Combine(parent, name);
    }

    private static IEnumerable<NtfsMftEntry> ReadVolume(string volume, CancellationToken ct)
    {
        using var reader = new NtfsMftReader(volume);
        foreach (var entry in reader.ReadEntries(ct)) yield return entry;
    }
}
