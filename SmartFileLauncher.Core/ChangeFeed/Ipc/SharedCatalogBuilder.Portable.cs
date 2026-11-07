using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Search.Paged;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

internal sealed partial class SharedCatalogBuilder
{
    internal List<Part> ReadPortableDirectories(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var result = new List<Part>();
        try { foreach (var root in roots) ReadPortablePart(root, false, [], result, ct); return result; }
        catch { foreach (var part in result) part.Store.Dispose(); throw; }
    }

    private void ReadPortablePart(string root, bool excludeRoot, HashSet<string> ancestors, List<Part> parts, CancellationToken ct)
    {
        var physical = caller.Run(_ => NtfsDirectoryReader.ReadPhysicalPath(root));
        if (ancestors.Any(ancestor => SameOrAncestor(physical, ancestor))) return;
        using var catalog = CreateCatalog(1, Expected(root), "path-identities", root);
        catalog.Accept(caller.Run(_ => PortableEntry(new DirectoryInfo(root), 1, 1)), addTerms: TokenIndexing.Enabled);
        var stack = new Stack<(string Path, ulong Parent)>();
        var links = new List<(string Path, string Parent)>();
        stack.Push((root, 1));
        while (stack.TryPop(out var current))
        {
            ct.ThrowIfCancellationRequested();
            IEnumerator<FileSystemInfo> iterator;
            try { iterator = caller.Run(_ => new DirectoryInfo(current.Path).EnumerateFileSystemInfos().GetEnumerator()); }
            catch (Exception failure) when (MissingOrDenied(failure)) { continue; }
            using (iterator)
            {
                while (true)
                {
                    ct.ThrowIfCancellationRequested();
                    NtfsMftEntry[] batch;
                    try { batch = caller.Run(_ => ReadPortableBatch(iterator, current.Parent)); }
                    catch (Exception failure) when (MissingOrDenied(failure)) { break; }
                    if (batch.Length == 0) break;
                    foreach (var item in batch)
                    {
                        var path = Path.Combine(current.Path, item.Name);
                        if (Excluded(path)) continue;
                        var entry = item with { FileReference = catalog.AllocateSyntheticReference() };
                        catalog.Accept(entry, addTerms: TokenIndexing.Enabled);
                        _count++;
                        if (!entry.IsDirectory) continue;
                        if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) links.Add((path, current.Path));
                        else stack.Push((path, entry.FileReference));
                    }
                    progress("Klasörler okunuyor ve kataloğa yerleştiriliyor", _count);
                }
            }
        }
        catalog.Finish(buildNameOrder: false);
        parts.Add(new(new CatalogStore(catalog, true), excludeRoot, false));
        foreach (var link in links)
        {
            try
            {
                var parent = caller.Run(_ => NtfsDirectoryReader.ReadPhysicalPath(link.Parent));
                ReadPart(link.Path, true, new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase) { physical, parent }, parts, ct);
            }
            catch (Exception failure) when (MissingOrDenied(failure)) { }
        }
    }

    internal static NtfsMftEntry PortableEntry(FileSystemInfo info, ulong reference, ulong parent)
    {
        info.Refresh();
        if (!info.Exists) throw new FileNotFoundException("Dosya tarama sırasında kaldırıldı.", info.FullName);
        var directory = (info.Attributes & FileAttributes.Directory) != 0;
        return new(reference, parent, info.Name, directory, info.Attributes, directory ? 0 : ((FileInfo)info).Length,
            info.CreationTimeUtc.Ticks, info.LastWriteTimeUtc.Ticks);
    }

    internal static NtfsMftEntry[] ReadPortableBatch(IEnumerator<FileSystemInfo> entries, ulong parent)
    {
        var result = new List<NtfsMftEntry>(512);
        while (result.Count < 512 && entries.MoveNext())
        {
            try { result.Add(PortableEntry(entries.Current, 0, parent)); }
            catch (FileNotFoundException) { }
        }
        return result.ToArray();
    }
}
