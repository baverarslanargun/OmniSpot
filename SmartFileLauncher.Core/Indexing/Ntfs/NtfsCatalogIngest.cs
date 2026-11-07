using SmartFileLauncher.Core.Search.Paged;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

internal sealed class NtfsCatalogIngest
{
    private readonly Catalog[] _catalogs;
    private readonly Catalog? _volume;
    private readonly bool _addTerms;
    private readonly Dictionary<ulong, int> _scope = [];
    private readonly Dictionary<ulong, List<NtfsMftEntry>> _waiting = [];
    internal int Accepted { get; private set; }

    internal NtfsCatalogIngest(Catalog[] catalogs, bool addTerms = true)
    {
        _catalogs = catalogs;
        _addTerms = addTerms;
        for (var i = 0; i < catalogs.Length; i++) _scope.Add(catalogs[i].Data.RootFrn, i);
        _volume = catalogs.SingleOrDefault(catalog => Path.GetPathRoot(catalog.Data.RootPath) == catalog.Data.RootPath);
    }

    internal void Accept(NtfsMftEntry entry)
    {
        if (_volume is not null) { _volume.Accept(entry, addTerms: _addTerms); Accepted++; return; }
        if (entry.IsDirectory && (entry.FileReference & 0x0000FFFFFFFFFFFFUL) == 5 && !_scope.ContainsKey(entry.FileReference))
            _scope[entry.FileReference] = -1;
        var target = -1;
        var resolved = entry.IsDirectory && _scope.TryGetValue(entry.FileReference, out target);
        if (!resolved && !_scope.TryGetValue(entry.ParentFileReference, out target))
        {
            if (!_waiting.TryGetValue(entry.ParentFileReference, out var waiting)) _waiting.Add(entry.ParentFileReference, waiting = []);
            waiting.Add(entry);
            return;
        }
        Deliver(entry, target);
    }

    private void Deliver(NtfsMftEntry entry, int target)
    {
        if (target >= 0) { _catalogs[target].Accept(entry, addTerms: _addTerms); Accepted++; }
        if (!entry.IsDirectory) return;
        _scope[entry.FileReference] = target;
        var ready = new Queue<ulong>();
        ready.Enqueue(entry.FileReference);
        while (ready.TryDequeue(out var parent))
        {
            if (!_waiting.Remove(parent, out var children)) continue;
            foreach (var child in children)
            {
                var scope = child.IsDirectory && _scope.TryGetValue(child.FileReference, out var own) ? own : _scope[parent];
                if (scope >= 0) { _catalogs[scope].Accept(child, addTerms: _addTerms); Accepted++; }
                if (child.IsDirectory) { _scope[child.FileReference] = scope; ready.Enqueue(child.FileReference); }
            }
        }
    }

    internal void Complete(Func<ulong, IReadOnlyList<NtfsMftEntry>> current, CancellationToken ct)
    {
        var requested = new HashSet<ulong>();
        while (_waiting.Count != 0)
        {
            ct.ThrowIfCancellationRequested();
            var parents = _waiting.Keys.Where(reference => !requested.Contains(reference)).ToArray();
            if (parents.Length == 0) throw new InvalidDataException("MFT kapsamının üst klasör zinciri döngü içeriyor.");
            foreach (var parent in parents)
            {
                if (!_waiting.ContainsKey(parent)) continue;
                requested.Add(parent);
                var entries = current(parent);
                if (entries.Count != 0) { foreach (var entry in entries) Accept(entry); continue; }
                var pending = _waiting[parent];
                _waiting.Remove(parent);
                foreach (var child in pending)
                {
                    var live = current(child.FileReference);
                    if (live.Any(entry => entry.ParentFileReference == parent)) throw new InvalidDataException("MFT kaydı eksik üst klasöre bağlı.");
                    foreach (var entry in live) Accept(entry);
                }
            }
        }
        foreach (var catalog in _catalogs) RepairParents(catalog, current, ct, _addTerms);
    }

    private static void RepairParents(Catalog catalog, Func<ulong, IReadOnlyList<NtfsMftEntry>> current, CancellationToken ct, bool addTerms)
    {
        var requested = new HashSet<ulong>();
        while (true)
        {
            var missing = catalog.ActiveIds().Where(id => id != Catalog.RootId && !catalog.Alive((uint)catalog.Row(id).Parent))
                .GroupBy(id => catalog.Row((uint)catalog.Row(id).Parent).Frn).ToArray();
            if (missing.Length == 0) return;
            foreach (var group in missing)
            {
                ct.ThrowIfCancellationRequested();
                if (!requested.Add(group.Key)) throw new InvalidDataException("MFT üst klasör uzlaştırması ilerlemiyor.");
                var parent = current(group.Key);
                if (parent.Count != 0)
                {
                    foreach (var entry in parent)
                    {
                        if (!entry.IsDirectory) throw new InvalidDataException("MFT üst kimliği klasör değil.");
                        catalog.Accept(entry, addTerms: addTerms);
                    }
                    continue;
                }
                foreach (var child in group)
                {
                    if (!catalog.Alive(child)) continue;
                    var live = current(catalog.Row(child).Frn);
                    if (live.Any(entry => entry.ParentFileReference == group.Key &&
                        entry.Name.Equals(catalog.Name(child), StringComparison.OrdinalIgnoreCase)))
                        throw new InvalidDataException("MFT dosyası eksik üst klasöre bağlı.");
                    if (catalog.IsDirectory(child)) throw new InvalidDataException("MFT dizin silinmesi tekrar uzlaştırılmalı.");
                    catalog.RemoveVerifiedAlias(child);
                    foreach (var entry in live) catalog.Accept(entry, addTerms: addTerms);
                }
                catalog.DropEmptyPlaceholder(group.Key);
            }
        }
    }
}
