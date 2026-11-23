using SmartFileLauncher.Core.Index;

namespace SmartFileLauncher.Core.Indexing.Folders;

internal static class FolderVolumeSync
{
    internal static List<IndexRecordState> Collect(FileIndex index, IReadOnlyList<string> roots, IReadOnlyCollection<string> dirty,
        FolderReferences ids, CancellationToken cancellationToken)
    {
        var states = new List<IndexRecordState>();
        var queue = new Queue<string>();
        var queued = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        void Enqueue(string? path)
        {
            if (path is null || Normalize(path) is not { } full || !Inside(roots, full) || !queued.Add(full)) return;
            queue.Enqueue(full);
        }
        foreach (var path in dirty) Enqueue(path);
        var items = new List<FolderItem>();
        var handled = new HashSet<ulong>();
        while (queue.Count > 0)
        {
            var batch = new List<string>(queue.Count);
            while (queue.TryDequeue(out var next)) batch.Add(next);
            var listings = index.ListFolders(batch, cancellationToken);
            foreach (var path in batch)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (!listings.TryGetValue(path, out var listing))
                {
                    Enqueue(FolderVolumeScanner.Parent(path));
                    continue;
                }
                if (!handled.Add(listing.Reference)) continue;
                if (!FolderVolumeScanner.TryRead(path, items))
                {
                    var parent = FolderVolumeScanner.Parent(path);
                    if (parent is not null && Inside(roots, parent))
                    {
                        Enqueue(parent);
                        continue;
                    }
                    items.Clear();
                }
                Diff(path, listing, items, ids, states, cancellationToken);
            }
        }
        return states;
    }

    private static void Diff(string path, IndexFolderListing listing, List<FolderItem> items, FolderReferences ids,
        List<IndexRecordState> states, CancellationToken cancellationToken)
    {
        var known = new Dictionary<string, IndexFolderChild>(listing.Children.Count, StringComparer.OrdinalIgnoreCase);
        foreach (var child in listing.Children) known.TryAdd(child.Name, child);
        var kept = new HashSet<ulong>();
        foreach (var item in items)
        {
            if (known.TryGetValue(item.Name, out var existing) && existing.IsDirectory == item.IsDirectory && kept.Add(existing.Reference))
            {
                var entry = item.ToEntry(existing.Reference, listing.Reference);
                if (!FileIndex.SameMetadata(existing, entry)) states.Add(new(existing.Reference, [entry]));
                continue;
            }
            var reference = ids.Take();
            states.Add(new(reference, [item.ToEntry(reference, listing.Reference)]));
            if (item.Descends) AddTree(Path.Join(path, item.Name), reference, ids, states, cancellationToken);
        }
        foreach (var child in listing.Children)
            if (!kept.Contains(child.Reference)) states.Add(new(child.Reference, []));
    }

    private static void AddTree(string path, ulong reference, FolderReferences ids, List<IndexRecordState> states,
        CancellationToken cancellationToken)
    {
        var pending = new Queue<(string Path, ulong Reference)>();
        pending.Enqueue((path, reference));
        var items = new List<FolderItem>();
        while (pending.TryDequeue(out var folder))
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (!FolderVolumeScanner.TryRead(folder.Path, items)) continue;
            foreach (var item in items)
            {
                var child = ids.Take();
                states.Add(new(child, [item.ToEntry(child, folder.Reference)]));
                if (item.Descends) pending.Enqueue((Path.Join(folder.Path, item.Name), child));
            }
        }
    }

    private static bool Inside(IReadOnlyList<string> roots, string path) => roots.Any(root => FolderVolumeScanner.Contains(root, path));

    private static string? Normalize(string path)
    {
        try { return FolderVolumeScanner.Full(path); }
        catch (Exception failure) when (failure is ArgumentException or NotSupportedException or PathTooLongException) { return null; }
    }
}
