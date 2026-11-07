using System.Runtime.Versioning;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Search.Paged;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
internal sealed partial class SharedCatalogBuilder(ChangeFeedCaller caller, string directory, string excludedPath,
    Action<string, int> progress, Action<string, IReadOnlyList<UsnDirectoryEntry>>? directoryMap = null,
    IndexBuildOptions? buildOptions = null)
{
    private readonly IndexBuildOptions _buildOptions = IndexBuildOptions.Normalize(buildOptions);
    internal sealed record Part(CatalogStore Store, bool ExcludeRoot, bool NativeIdentity, bool AllNames = false);
    private int _count;

    internal SharedCatalogBuildMetrics? Metrics { get; private set; }

    internal List<Part> ReadMft(IReadOnlyList<ChangeFeedSubscribedRoot> roots, CancellationToken ct)
    {
        var results = new List<Part>();
        var drafts = new List<Catalog>();
        var links = new List<string>();
        double ingestMs = 0, prepareMs = 0, commitMs = 0;
        try
        {
            foreach (var volume in roots.GroupBy(root => UsnVolumeJournalReader.ResolveVolumeRoot(root.RootPath), StringComparer.OrdinalIgnoreCase))
            {
                using var journal = new UsnVolumeJournalReader(volume.Key);
                var boundary = journal.QueryJournal();
                var targets = volume.Select(root => CreateCatalog(Convert.ToUInt64(root.Identity.NodeId[2..], 16),
                    Expected(root.RootPath), root.Identity.VolumeId, root.RootPath)).ToArray();
                drafts.AddRange(targets);
                var ingest = new NtfsCatalogIngest(targets, TokenIndexing.Enabled);
                var clock = System.Diagnostics.Stopwatch.StartNew();
                using var raw = new NtfsMftReader(volume.Key);
                var baseCount = _count;
                var processed = 0;
                var reparses = new Dictionary<ulong, uint?>();
                foreach (var entry in raw.ReadEntries(_buildOptions, ct))
                {
                    ingest.Accept(entry);
                    if (entry.IsDirectory && (entry.Attributes & FileAttributes.ReparsePoint) != 0)
                        reparses[entry.FileReference] = entry.ReparseTag;
                    if ((++processed & 8191) == 0) progress("MFT okunuyor ve kataloğa yerleştiriliyor", baseCount + ingest.Accepted);
                }
                _count += ingest.Accepted;
                ingestMs += clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                progress("Katalog bağlantıları doğrulanıyor", _count);
                ingest.Complete(reference => raw.ReadCurrent(reference, ct), ct);
                _count = baseCount + targets.Sum(catalog => catalog.Data.EntryCount);
                var end = journal.QueryJournal();
                if (end.JournalId != boundary.JournalId || end.NextUsn < boundary.NextUsn ||
                    boundary.NextUsn < Math.Max(end.FirstUsn, end.LowestValidUsn))
                    throw new InvalidDataException("MFT okunurken USN sürekliliği kayboldu.");
                foreach (var catalog in targets)
                {
                    var privateId = new Reader(catalog).FindPath(excludedPath);
                    if (privateId != 0) catalog.RemoveVerifiedAlias(privateId);
                    catalog.Audit();
                    var map = new List<UsnDirectoryEntry>(catalog.DirectoryCount);
                    for (uint row = 1; row < catalog.Data.Count(Image.Dirs); row++)
                    {
                        var id = row * 2 + 1;
                        if (id == Catalog.RootId || !catalog.Alive(id)) continue;
                        var item = catalog.Row(id);
                        map.Add(new(UsnFileReference.FromNtfs(item.Frn), catalog.Name(id),
                            UsnFileReference.FromNtfs(catalog.Row((uint)item.Parent).Frn)));
                        if (((FileAttributes)catalog.Meta(id).Attributes & FileAttributes.ReparsePoint) == 0) continue;
                        var tag = reparses.GetValueOrDefault(item.Frn);
                        if (tag is { } known && (known & 0x20000000) == 0) continue;
                        var path = catalog.FullPath(id);
                        try { if (tag is not null || NtfsDirectoryReader.IsNameSurrogate(path)) links.Add(path); }
                        catch (Exception failure) when (MissingOrDenied(failure)) { }
                    }
                    directoryMap?.Invoke(catalog.Data.RootPath, map);
                }
                prepareMs += clock.Elapsed.TotalMilliseconds;
                clock.Restart();
                progress("Katalog kaydediliyor", _count);
                foreach (var catalog in targets)
                {
                    ct.ThrowIfCancellationRequested();
                    catalog.Data.Commit(0);
                    results.Add(new(new CatalogStore(catalog, true), false, true, true));
                }
                commitMs += clock.Elapsed.TotalMilliseconds;
            }
            var linkClock = System.Diagnostics.Stopwatch.StartNew();
            foreach (var link in links) AddLink(link, results, ct);
            Metrics = new(ingestMs, prepareMs, commitMs, linkClock.Elapsed.TotalMilliseconds);
            return results;
        }
        catch
        {
            foreach (var result in results) result.Store.Dispose();
            throw;
        }
        finally { foreach (var draft in drafts) draft.Dispose(); }
    }

    internal List<Part> ReadDirectories(IReadOnlyList<string> roots, CancellationToken ct)
    {
        var result = new List<Part>();
        try
        {
            foreach (var root in roots) ReadPart(root, false, [], result, ct);
            return result;
        }
        catch { foreach (var part in result) part.Store.Dispose(); throw; }
    }

    internal List<Part> ReadLink(string path, CancellationToken ct)
    {
        var parts = new List<Part>();
        try { AddLink(path, parts, ct); return parts; }
        catch { foreach (var part in parts) part.Store.Dispose(); throw; }
    }

    private void AddLink(string path, List<Part> parts, CancellationToken ct)
    {
        try
        {
            var ancestors = caller.Run(_ =>
            {
                var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(Path.TrimEndingDirectorySeparator(parent)))
                    paths.Add(NtfsDirectoryReader.ReadPhysicalPath(parent));
                return paths;
            });
            ReadPart(path, true, ancestors, parts, ct);
        }
        catch (Exception failure) when (MissingOrDenied(failure)) { }
    }

    private void ReadPart(string root, bool excludeRoot, HashSet<string> ancestors, List<Part> parts, CancellationToken ct)
    {
        try { ReadNativePart(root, excludeRoot, ancestors, parts, ct); }
        catch (Exception failure) when (NativeUnsupported(failure)) { ReadPortablePart(root, excludeRoot, ancestors, parts, ct); }
    }

    private void ReadNativePart(string root, bool excludeRoot, HashSet<string> ancestors, List<Part> parts, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        using var opened = caller.Run(_ => new NtfsDirectoryReader(root));
        if (!caller.Run(_ => opened.HasNtfsIdentity())) throw new NotSupportedException("Bu konum NTFS dosya kimliği taşımıyor.");
        if (ancestors.Any(ancestor => SameOrAncestor(opened.PhysicalPath, ancestor))) return;
        var lineage = new HashSet<string>(ancestors, StringComparer.OrdinalIgnoreCase) { opened.PhysicalPath };
        using var catalog = CreateCatalog(opened.Root.FileReference, Expected(root), opened.VolumeIdentity, root);
        catalog.Accept(opened.Root, addTerms: TokenIndexing.Enabled);
        _count++;
        var stack = new Stack<(string Path, ulong Frn)>();
        var links = new List<(string Path, string ParentPhysical)>();
        var map = new List<UsnDirectoryEntry>();
        ReadDirectory(opened, root);
        while (stack.TryPop(out var next))
        {
            ct.ThrowIfCancellationRequested();
            NtfsDirectoryReader current;
            try { current = caller.Run(_ => new NtfsDirectoryReader(next.Path)); }
            catch (Exception failure) when (MissingOrDenied(failure)) { continue; }
            using (current)
            {
                if (current.Root.FileReference != next.Frn) continue;
                ReadDirectory(current, next.Path);
            }
        }
        progress("Katalog kaydediliyor", _count);
        catalog.Finish(buildNameOrder: false);
        parts.Add(new(new CatalogStore(catalog, true), excludeRoot, true));
        if (!excludeRoot) directoryMap?.Invoke(root, map);
        foreach (var link in links)
        {
            try { ReadPart(link.Path, true, new HashSet<string>(lineage, StringComparer.OrdinalIgnoreCase) { link.ParentPhysical }, parts, ct); }
            catch (Exception failure) when (MissingOrDenied(failure)) { }
        }

        void ReadDirectory(NtfsDirectoryReader reader, string path)
        {
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                var entries = caller.Run(_ => reader.ReadNext());
                if (entries.Count == 0) return;
                foreach (var entry in entries)
                {
                    var child = Path.Combine(path, entry.Name);
                    if (Excluded(child)) continue;
                    catalog.Accept(entry, addTerms: TokenIndexing.Enabled);
                    _count++;
                    if (!entry.IsDirectory) continue;
                    map.Add(new(UsnFileReference.FromNtfs(entry.FileReference), entry.Name, UsnFileReference.FromNtfs(entry.ParentFileReference)));
                    if ((entry.Attributes & FileAttributes.ReparsePoint) != 0) links.Add((child, reader.PhysicalPath));
                    else stack.Push((child, entry.FileReference));
                }
                progress("Klasörler okunuyor ve kataloğa yerleştiriliyor", _count);
            }
        }
    }

    internal static bool MissingOrDenied(Exception failure) => failure is UnauthorizedAccessException or FileNotFoundException or DirectoryNotFoundException ||
        failure is IOException { InnerException: System.ComponentModel.Win32Exception { NativeErrorCode: 2 or 3 or 5 } };
    private static bool NativeUnsupported(Exception failure) => failure is NotSupportedException or
        System.ComponentModel.Win32Exception { NativeErrorCode: 1 or 50 or 87 } or
        IOException { InnerException: System.ComponentModel.Win32Exception { NativeErrorCode: 1 or 50 or 87 } };
    private bool Excluded(string path) => UsnRootScope.TryCanonicalize(excludedPath, path, out _);
    private string NewDirectory() => Path.Combine(directory, "part-" + Guid.NewGuid().ToString("N"));
    private Catalog CreateCatalog(ulong reference, int expected, string volume, string root)
    {
        var catalog = Catalog.Create(NewDirectory(), reference, expected, volume, root);
        catalog.Data.TermsReady = TokenIndexing.Enabled;
        return catalog;
    }
    private static int Expected(string path) => Path.GetPathRoot(path) == path ? 4_500_000 : 65_536;
    private static bool SameOrAncestor(string possibleAncestor, string path) =>
        path.Equals(possibleAncestor, StringComparison.OrdinalIgnoreCase) || path.StartsWith(possibleAncestor.TrimEnd('\\') + "\\", StringComparison.OrdinalIgnoreCase);
}
