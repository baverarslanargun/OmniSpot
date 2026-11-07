using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

public sealed partial class NtfsRootInventory
{
    internal IEnumerable<IndexInventoryEntry> ReadStreaming(IReadOnlyList<ChangeFeedSubscribedRoot> roots,
        CancellationToken ct, NtfsInventorySecurityGuard security,
        Action<string, IReadOnlyList<UsnDirectoryEntry>>? directoryMap = null)
    {
        foreach (var volume in roots.GroupBy(root => Path.GetPathRoot(root.RootPath)!, StringComparer.OrdinalIgnoreCase))
        {
            security.BeginVolume(volume.Key);
            var volumeId = volume.First().Identity.VolumeId;
            if (volume.Any(root => root.Identity.VolumeId != volumeId)) throw new InvalidDataException("Birim kimlikleri farklı.");
            var selected = volume.ToDictionary(root => Convert.ToUInt64(root.Identity.NodeId[2..], 16),
                root => Path.TrimEndingDirectorySeparator(root.RootPath));
            var prefixes = selected.ToDictionary(pair => pair.Key, pair => (string?)pair.Value);
            var directories = new Dictionary<ulong, NtfsMftEntry>();
            var waitingDirectories = new Dictionary<ulong, List<ulong>>();
            var waitingFiles = new Dictionary<ulong, List<NtfsMftEntry>>();
            var ready = new Queue<IndexInventoryEntry>();
            var maps = volume.ToDictionary(root => root.RootPath, _ => new List<UsnDirectoryEntry>(), StringComparer.OrdinalIgnoreCase);

            IndexInventoryEntry Entry(NtfsMftEntry entry, string path)
            {
                security.Include(volume.Key, entry.FileReference);
                return new(path, entry.IsDirectory, entry.Attributes, entry.SizeBytes, entry.CreatedTimeUtc, entry.LastWriteTimeUtc,
                    new(volumeId, entry.FileReference, entry.ParentFileReference, entry.Name));
            }

            void Resolve(ulong first)
            {
                var queue = new Queue<ulong>();
                queue.Enqueue(first);
                while (queue.TryDequeue(out var reference))
                {
                    ct.ThrowIfCancellationRequested();
                    var entry = directories[reference];
                    string? path;
                    if (selected.TryGetValue(reference, out var chosen)) path = chosen;
                    else if ((reference & 0xFFFFFFFFFFFFUL) == 5) path = null;
                    else if (prefixes.TryGetValue(entry.ParentFileReference, out var parent)) path = parent is null ? null : Join(parent, entry.Name);
                    else
                    {
                        if (!waitingDirectories.TryGetValue(entry.ParentFileReference, out var pending)) waitingDirectories[entry.ParentFileReference] = pending = [];
                        pending.Add(reference);
                        continue;
                    }
                    if (path is not null)
                    {
                        ready.Enqueue(Entry(entry, path));
                        if (!selected.ContainsKey(reference))
                        {
                            var owner = maps.First(pair => UsnRootScope.TryCanonicalize(pair.Key, path, out _)).Value;
                            owner.Add(new(UsnFileReference.FromNtfs(reference), entry.Name, UsnFileReference.FromNtfs(entry.ParentFileReference)));
                        }
                    }
                    var prefix = !selected.ContainsKey(reference) && (entry.Attributes & FileAttributes.ReparsePoint) != 0 ? null : path;
                    prefixes[reference] = prefix;
                    if (waitingFiles.Remove(reference, out var files) && prefix is not null)
                        foreach (var file in files) ready.Enqueue(Entry(file, Join(prefix, file.Name)));
                    if (waitingDirectories.Remove(reference, out var children)) foreach (var child in children) queue.Enqueue(child);
                }
            }

            foreach (var entry in _read(volume.Key, ct))
            {
                ct.ThrowIfCancellationRequested();
                if (entry.IsDirectory)
                {
                    if (!directories.TryAdd(entry.FileReference, entry)) throw new InvalidDataException("Dizin kimliği yinelendi.");
                    Resolve(entry.FileReference);
                }
                else if (prefixes.TryGetValue(entry.ParentFileReference, out var parent))
                {
                    if (parent is not null) ready.Enqueue(Entry(entry, Join(parent, entry.Name)));
                }
                else
                {
                    if (!waitingFiles.TryGetValue(entry.ParentFileReference, out var files)) waitingFiles[entry.ParentFileReference] = files = [];
                    files.Add(entry);
                }
                while (ready.TryDequeue(out var next)) yield return next;
            }
            foreach (var root in selected.Keys)
            {
                var reference = root;
                for (var depth = 0; ; depth++)
                {
                    if (depth >= 512 || !directories.TryGetValue(reference, out var ancestor)) throw new InvalidDataException("MFT kök üst zinciri eksik veya döngülü.");
                    security.Include(volume.Key, reference);
                    if ((reference & 0xFFFFFFFFFFFFUL) == 5) break;
                    reference = ancestor.ParentFileReference;
                }
            }
            foreach (var unresolved in waitingDirectories.Keys.Concat(waitingFiles.Keys).Distinct())
            {
                var reference = unresolved;
                var visited = new HashSet<ulong>();
                while (directories.TryGetValue(reference, out var ancestor))
                {
                    if (!visited.Add(reference) || visited.Count >= 512) throw new InvalidDataException("MFT üst zinciri döngülü.");
                    reference = ancestor.ParentFileReference;
                }
                security.ExpectDirectoryChange(volume.Key, reference);
            }
            foreach (var pair in maps) directoryMap?.Invoke(pair.Key, pair.Value);
        }
    }
}
