using System.Runtime.Versioning;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search.Paged;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
internal sealed class SharedCatalogReconciler(ChangeFeedCaller caller, string excludedPath, Action<string>? linkDiscovered = null,
    bool nativeIdentity = true, bool allNames = false, Func<string, bool>? wasLinkedDirectory = null,
    Action<int, int, ChangeFeedEventDto>? progress = null, SharedCatalogProfile? profile = null)
{
    private Catalog? _readerCatalog;
    private Reader? _reader;
    private Reader ReaderFor(Catalog catalog)
    {
        if (!ReferenceEquals(_readerCatalog, catalog))
        {
            _readerCatalog = catalog;
            _reader = new Reader(catalog, cachePaths: true);
        }
        return _reader!;
    }
    private uint FindPath(Catalog catalog, string path)
    {
        using var timing = profile?.Measure("path", path);
        return ReaderFor(catalog).FindPath(path);
    }
    internal void Apply(Catalog catalog, IReadOnlyList<ChangeFeedEventDto> changes, CancellationToken ct)
    {
        for (var index = 0; index < changes.Count; index++)
        {
            var change = changes[index];
            ct.ThrowIfCancellationRequested();
            progress?.Invoke(index + 1, changes.Count, change);
            if (change.OldPath is { } oldPath && InRoot(catalog, oldPath) && InRoot(catalog, change.Path))
            {
                Rename(catalog, oldPath, change.Path, ct);
                if (change.RefreshSubtree && !allNames) Refresh(catalog, change.Path, ct, true);
            }
            else
            {
                if (change.OldPath is { } old && InRoot(catalog, old)) Refresh(catalog, old, ct, false);
                if (InRoot(catalog, change.Path)) Refresh(catalog, change.Path, ct, change.IsDirectory &&
                    (change.Kind != ChangeFeedEventKind.Modified || change.RefreshSubtree && !allNames));
            }
        }
    }

    internal void Repair(Catalog catalog, string path, CancellationToken ct) => Refresh(catalog, path, ct, true);

    private void Rename(Catalog catalog, string oldPath, string path, CancellationToken ct)
    {
        var reader = ReaderFor(catalog);
        var oldId = FindPath(catalog, oldPath);
        var parent = EnsureParent(catalog, path, ct);
        var entry = Read(catalog, path, parent);
        if (!nativeIdentity && oldId != 0 && entry is not null) entry = entry with { FileReference = catalog.Row(oldId).Frn };
        if (oldId != 0 && oldId != Catalog.RootId && entry is not null && catalog.Row(oldId).Frn == entry.FileReference)
        {
            var old = catalog.Row(oldId);
            using (profile?.Measure("mutation", path))
                catalog.Apply(new(old.Frn, catalog.Row((uint)old.Parent).Frn, catalog.Name(oldId), entry, catalog.Meta(oldId).OpenCount));
            Refresh(catalog, oldPath, ct, false);
        }
        else
        {
            Refresh(catalog, oldPath, ct, false);
            Refresh(catalog, path, ct, entry?.IsDirectory == true);
        }
    }

    private void Refresh(Catalog catalog, string path, CancellationToken ct, bool subtree)
    {
        ct.ThrowIfCancellationRequested();
        if (!InRoot(catalog, path) || Excluded(path)) return;
        var reader = ReaderFor(catalog);
        var id = FindPath(catalog, path);
        if (id == Catalog.RootId)
        {
            if (subtree) Scan(catalog, path, Catalog.RootId, ct);
            return;
        }
        var parent = EnsureParent(catalog, path, ct);
        var entry = Read(catalog, path, parent);
        if (entry is null)
        {
            if (id != 0) { using var timing = profile?.Measure("mutation", path); catalog.RemoveVerifiedAlias(id); }
            return;
        }
        if (allNames && entry.IsDirectory && id != 0 &&
            ((FileAttributes)catalog.Meta(id).Attributes & FileAttributes.ReparsePoint) != (entry.Attributes & FileAttributes.ReparsePoint)) subtree = true;
        var opened = id != 0 && catalog.Row(id).Frn == entry.FileReference ? catalog.Meta(id).OpenCount : 0;
        if (id != 0 && catalog.Row(id).Frn != entry.FileReference)
        { using var timing = profile?.Measure("mutation", path); catalog.RemoveVerifiedAlias(id); }
        using (profile?.Measure("mutation", path))
            catalog.Apply(new(entry.FileReference, entry.ParentFileReference, entry.Name, entry, opened));
        var link = entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && Metadata(() => NtfsDirectoryReader.IsNameSurrogate(path));
        if (allNames && entry.IsDirectory && !link && wasLinkedDirectory?.Invoke(path) == true) subtree = true;
        if (link)
        {
            foreach (var child in catalog.Children(catalog.Find(entry.FileReference))) catalog.RemoveVerifiedAlias(child);
            linkDiscovered?.Invoke(path);
        }
        if (subtree && entry.IsDirectory && !link)
            Scan(catalog, path, catalog.Find(entry.FileReference), ct);
    }

    private ulong EnsureParent(Catalog catalog, string path, CancellationToken ct)
    {
        if (string.Equals(Path.TrimEndingDirectorySeparator(path), Path.TrimEndingDirectorySeparator(catalog.Data.RootPath), StringComparison.OrdinalIgnoreCase)) return catalog.Data.RootFrn;
        var parentPath = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(path)) ?? throw new InvalidDataException("Üst klasör eksik.");
        if (!InRoot(catalog, parentPath)) throw new InvalidDataException("Güncelleme katalog kapsamı dışında.");
        var parent = FindPath(catalog, parentPath);
        if (parent == 0)
        {
            Refresh(catalog, parentPath, ct, false);
            parent = FindPath(catalog, parentPath);
        }
        if (parent == 0) return 0;
        if (allNames && ((FileAttributes)catalog.Meta(parent).Attributes & FileAttributes.ReparsePoint) != 0 &&
            Metadata(() => NtfsDirectoryReader.IsNameSurrogate(parentPath))) return 0;
        return catalog.Row(parent).Frn;
    }

    private NtfsMftEntry? Read(Catalog catalog, string path, ulong parent) => Metadata(() =>
    {
        using var timing = profile?.Measure("metadata", path);
        if (parent == 0 || !allNames && !ChangeFeedPathAuthorizer.ForCurrentCaller(catalog.Data.RootPath).CanReturnExistingPath(path)) return null;
        try
        {
            if (nativeIdentity) return NtfsDirectoryReader.ReadFile(path, parent);
            FileSystemInfo info = Directory.Exists(path) ? new DirectoryInfo(path) : new FileInfo(path);
            return AssignIdentity(catalog, path, SharedCatalogBuilder.PortableEntry(info, 0, parent));
        }
        catch (Exception failure) when (SharedCatalogBuilder.MissingOrDenied(failure)) { return null; }
    });

    private void Scan(Catalog catalog, string path, uint id, CancellationToken ct)
    {
        using var timing = profile?.Measure("scan", path);
        if (!nativeIdentity) { ScanPortable(catalog, path, id, ct); return; }
        NtfsDirectoryReader opened;
        try { opened = Metadata(() => new NtfsDirectoryReader(path)); }
        catch (Exception failure) when (SharedCatalogBuilder.MissingOrDenied(failure))
        {
            foreach (var child in catalog.Children(id)) catalog.RemoveVerifiedAlias(child);
            return;
        }
        using (opened)
        {
            if (opened.Root.FileReference != catalog.Row(id).Frn) throw new InvalidDataException("Onarılacak klasör kimliği değişti.");
            var unseen = catalog.Children(id).ToHashSet();
            var children = new List<(string Path, uint Id)>();
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var batch = Metadata(() => opened.ReadNext());
                if (batch.Count == 0) break;
                foreach (var entry in batch)
                {
                    var childPath = Path.Combine(path, entry.Name);
                    if (Excluded(childPath)) continue;
                    var prior = FindPath(catalog, childPath);
                    var openedCount = prior != 0 && catalog.Row(prior).Frn == entry.FileReference ? catalog.Meta(prior).OpenCount : 0;
                    catalog.Apply(new(entry.FileReference, entry.ParentFileReference, entry.Name, entry, openedCount));
                    var current = catalog.Resolve(entry);
                    unseen.Remove(current);
                    var link = entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0 && Metadata(() => NtfsDirectoryReader.IsNameSurrogate(childPath));
                    if (link)
                    {
                        foreach (var child in catalog.Children(current)) catalog.RemoveVerifiedAlias(child);
                        linkDiscovered?.Invoke(childPath);
                    }
                    if (entry.IsDirectory && !link) children.Add((childPath, current));
                }
            }
            foreach (var missing in unseen) if (catalog.Alive(missing)) catalog.RemoveVerifiedAlias(missing);
            foreach (var child in children) Scan(catalog, child.Path, child.Id, ct);
        }
    }

    private NtfsMftEntry AssignIdentity(Catalog catalog, string path, NtfsMftEntry entry)
    {
        var id = FindPath(catalog, path);
        var reference = id != 0 && catalog.IsDirectory(id) == entry.IsDirectory && catalog.Meta(id).Created == entry.CreatedTimeUtc
            ? catalog.Row(id).Frn : catalog.AllocateSyntheticReference();
        return entry with { FileReference = reference };
    }

    private void ScanPortable(Catalog catalog, string path, uint id, CancellationToken ct)
    {
        var unseen = catalog.Children(id).ToHashSet();
        var children = new List<(string Path, uint Id)>();
        try
        {
            using var entries = caller.Run(_ => new DirectoryInfo(path).EnumerateFileSystemInfos().GetEnumerator());
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var batch = caller.Run(_ => SharedCatalogBuilder.ReadPortableBatch(entries, catalog.Row(id).Frn));
                if (batch.Length == 0) break;
                foreach (var item in batch)
                {
                    var childPath = Path.Combine(path, item.Name);
                    if (Excluded(childPath)) continue;
                    var entry = AssignIdentity(catalog, childPath, item);
                    var prior = FindPath(catalog, childPath);
                    var opened = prior != 0 && catalog.Row(prior).Frn == entry.FileReference ? catalog.Meta(prior).OpenCount : 0;
                    catalog.Apply(new(entry.FileReference, entry.ParentFileReference, entry.Name, entry, opened));
                    var current = catalog.Resolve(entry);
                    unseen.Remove(current);
                    if (!entry.IsDirectory) continue;
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0)
                    {
                        foreach (var child in catalog.Children(current)) catalog.RemoveVerifiedAlias(child);
                        linkDiscovered?.Invoke(childPath);
                    }
                    else children.Add((childPath, current));
                }
            }
        }
        catch (Exception failure) when (SharedCatalogBuilder.MissingOrDenied(failure)) { }
        foreach (var missing in unseen) if (catalog.Alive(missing)) catalog.RemoveVerifiedAlias(missing);
        foreach (var child in children) ScanPortable(catalog, child.Path, child.Id, ct);
    }

    private T Metadata<T>(Func<T> read) => allNames ? read() : caller.Run(_ => read());

    private static bool InRoot(Catalog catalog, string path) => UsnRootScope.TryCanonicalize(catalog.Data.RootPath, path, out _);
    private bool Excluded(string path) => UsnRootScope.TryCanonicalize(excludedPath, path, out _);
}
