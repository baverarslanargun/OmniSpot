namespace SmartFileLauncher.Core.Index;

public enum IndexChangeKind
{
    Upsert,
    Delete,
    AddLink
}

public readonly record struct IndexChange(
    IndexChangeKind Kind,
    IndexEntry Entry,
    ulong PreviousParentReference = 0,
    string? PreviousName = null)
{
    public static IndexChange Upsert(IndexEntry entry, ulong previousParentReference = 0, string? previousName = null) =>
        new(IndexChangeKind.Upsert, entry, previousParentReference, previousName);

    public static IndexChange Delete(ulong fileReference, ulong parentReference, string name) =>
        new(IndexChangeKind.Delete, new IndexEntry(fileReference, parentReference, name, 0, 0, 0));

    public static IndexChange AddLink(IndexEntry entry) => new(IndexChangeKind.AddLink, entry);
}

public sealed record IndexApplyResult(int Applied, int Skipped);

public sealed record IndexRecordState(ulong FileReference, IReadOnlyList<IndexEntry> Links);

internal sealed record IndexChangeImpact(IReadOnlyList<string>? Names, bool IncludesDirectories);

public sealed partial class FileIndex
{
    public IndexApplyResult Apply(IReadOnlyList<IndexChange> changes, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(changes);
        if (changes.Count == 0) return new(0, 0);
        _lock.EnterWriteLock();
        try { return ApplyCore(changes, cancellationToken); }
        finally
        {
            _lock.ExitWriteLock();
            ReleaseIfCompacted();
        }
    }

    public IndexApplyResult Reconcile(IReadOnlyList<IndexRecordState> records, CancellationToken cancellationToken = default) =>
        Reconcile(records, false, out _, cancellationToken);

    internal IndexApplyResult Reconcile(IReadOnlyList<IndexRecordState> records, out IndexChangeImpact? impact,
        CancellationToken cancellationToken = default) => Reconcile(records, true, out impact, cancellationToken);

    private IndexApplyResult Reconcile(IReadOnlyList<IndexRecordState> records, bool captureImpact,
        out IndexChangeImpact? impact, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(records);
        impact = null;
        if (records.Count == 0) return new(0, 0);
        _lock.EnterWriteLock();
        try
        {
            var changes = new List<IndexChange>();
            HashSet<string>? affectedNames = captureImpact ? new(StringComparer.Ordinal) : null;
            var includesDirectories = false;
            void Track(string name, bool directory)
            {
                if (!captureImpact) return;
                includesDirectories |= directory;
                affectedNames?.Add(name);
                if (affectedNames?.Count > 4096) affectedNames = null;
            }
            foreach (var record in records)
            {
                cancellationToken.ThrowIfCancellationRequested();
                if (IsHiddenRecord(record.FileReference) || record.FileReference == RootReference) continue;
                var current = record.Links.Where(link => link.FileReference == record.FileReference && !IsHiddenRecord(link.ParentReference)).ToList();
                var existing = RowsOf(record.FileReference)
                    .Select(row => (Parent: _parent[row] == NoRow ? 0UL : _frn[_parent[row]], Name: _names.Text(_name[row]), Directory: IsDirectory(row)))
                    .ToList();
                foreach (var stale in StaleRowsOf(record.FileReference))
                {
                    Track(_names.Text(_name[stale]), IsDirectory(stale));
                    changes.Add(IndexChange.Delete(_frn[stale], _parent[stale] == NoRow ? 0UL : _frn[_parent[stale]], _names.Text(_name[stale])));
                }
                foreach (var link in current) Track(link.Name, false);
                foreach (var link in existing) Track(link.Name, false);
                if (captureImpact && (current.Any(link => link.IsDirectory) || existing.Any(link => link.Directory)) &&
                    (current.Count != existing.Count || current.Any(link => !existing.Any(old =>
                        old.Parent == link.ParentReference && old.Name == link.Name && old.Directory == link.IsDirectory))))
                    includesDirectories = true;
                var missing = current.Where(link => !existing.Any(item => item.Parent == link.ParentReference &&
                    string.Equals(item.Name, link.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                var extra = existing.Where(item => !current.Any(link => item.Parent == link.ParentReference &&
                    string.Equals(item.Name, link.Name, StringComparison.OrdinalIgnoreCase))).ToList();
                foreach (var link in current)
                {
                    var matched = existing.FirstOrDefault(item => item.Parent == link.ParentReference &&
                        string.Equals(item.Name, link.Name, StringComparison.OrdinalIgnoreCase));
                    if (matched.Name is not null)
                        changes.Add(IndexChange.Upsert(link, matched.Parent, matched.Name));
                }
                for (var i = 0; i < missing.Count; i++)
                {
                    if (i < extra.Count) changes.Add(IndexChange.Upsert(missing[i], extra[i].Parent, extra[i].Name));
                    else changes.Add(existing.Count == 0 && i == 0 ? IndexChange.Upsert(missing[i]) : IndexChange.AddLink(missing[i]));
                }
                for (var i = missing.Count; i < extra.Count; i++)
                    changes.Add(IndexChange.Delete(record.FileReference, extra[i].Parent, extra[i].Name));
            }
            var result = ApplyCore(changes, cancellationToken);
            if (captureImpact && result.Applied > 0)
                impact = new(affectedNames?.ToArray(), includesDirectories);
            return result;
        }
        finally
        {
            _lock.ExitWriteLock();
            ReleaseIfCompacted();
        }
    }

    private IndexApplyResult ApplyCore(IReadOnlyList<IndexChange> changes, CancellationToken cancellationToken)
    {
        var touched = new HashSet<uint>();
        var moved = new HashSet<uint>();
        var deletedFolders = new HashSet<uint>();
        var applied = 0;
        var skipped = 0;
        var waiting = new List<IndexChange>();
        foreach (var change in changes)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (IsHiddenRecord(change.Entry.FileReference) || change.Entry.FileReference == RootReference) { skipped++; continue; }
            bool? done = change.Kind == IndexChangeKind.Delete
                ? ApplyDelete(change, touched, deletedFolders)
                : ApplyUpsert(change, touched, moved);
            if (done is null) waiting.Add(change);
            else if (done.Value) applied++;
            else skipped++;
        }
        while (waiting.Count > 0)
        {
            var still = new List<IndexChange>();
            foreach (var change in waiting)
            {
                var done = ApplyUpsert(change, touched, moved);
                if (done is null) still.Add(change);
                else if (done.Value) applied++;
                else skipped++;
            }
            if (still.Count == waiting.Count)
            {
                skipped += still.Count;
                break;
            }
            waiting = still;
        }
        if (deletedFolders.Count > 0)
            foreach (var row in Descendants(deletedFolders))
            {
                touched.Add(row);
                Kill(row);
            }
        if (moved.Count > 0)
            foreach (var row in Descendants(moved)) touched.Add(row);
        UpdateOrders(touched);
        if (_dead > 65536 && _dead > _count / 8) Compact(cancellationToken);
        return new(applied, skipped);
    }

    private bool ApplyDelete(IndexChange change, HashSet<uint> touched, HashSet<uint> deletedFolders)
    {
        var target = SelectLink(change.Entry.FileReference, change.Entry.ParentReference, change.Entry.Name);
        if (target == NoRow) return false;
        touched.Add(target);
        if (IsDirectory(target)) deletedFolders.Add(target);
        Kill(target);
        return true;
    }

    private bool? ApplyUpsert(IndexChange change, HashSet<uint> touched, HashSet<uint> moved)
    {
        var entry = change.Entry;
        var parent = FindDirectory(entry.ParentReference);
        if (parent == NoRow) return null;
        var changed = false;
        foreach (var stale in StaleRowsOf(entry.FileReference).ToArray())
        {
            changed = true;
            touched.Add(stale);
            if (IsDirectory(stale)) moved.Add(stale);
            Kill(stale);
        }
        var row = change.PreviousName is not null
            ? SelectLink(entry.FileReference, change.PreviousParentReference, change.PreviousName)
            : NoRow;
        if (row == NoRow && change.Kind == IndexChangeKind.AddLink) row = ExactLink(entry.FileReference, parent, entry.Name);
        else if (row == NoRow) row = SelectLink(entry.FileReference, entry.ParentReference, entry.Name);
        if (row != NoRow && IsDirectory(row) != entry.IsDirectory)
        {
            touched.Add(row);
            Kill(row);
            row = NoRow;
        }
        if (row == NoRow)
        {
            row = AddRow(entry);
            _parent[row] = parent;
            touched.Add(row);
            return true;
        }
        var name = _names.Intern(entry.Name);
        var attributes = (uint)entry.Attributes & ~DeadBit;
        var size = entry.IsDirectory ? 0 : entry.SizeBytes;
        var created = CreatedSeconds(entry.CreatedTimeUtc);
        if (name != _name[row] || parent != _parent[row])
        {
            changed = true;
            touched.Add(row);
            if (IsDirectory(row)) moved.Add(row);
            _name[row] = name;
            _parent[row] = parent;
        }
        foreach (var link in RowsOf(entry.FileReference).ToArray())
        {
            if (_size[link] == size && _modified[link] == entry.LastWriteTimeUtc && _attributes[link] == attributes &&
                _created[link] == created) continue;
            changed = true;
            touched.Add(link);
            _size[link] = size;
            _modified[link] = entry.LastWriteTimeUtc;
            _created[link] = created;
            _attributes[link] = attributes;
        }
        return changed;
    }

    private uint ExactLink(ulong reference, uint parent, string name)
    {
        foreach (var row in RowsOf(reference))
            if (_parent[row] == parent && string.Equals(_names.Text(_name[row]), name, StringComparison.OrdinalIgnoreCase))
                return row;
        return NoRow;
    }

    private uint SelectLink(ulong reference, ulong parentReference, string name)
    {
        uint only = NoRow;
        var count = 0;
        var parent = parentReference == 0 ? NoRow : FindDirectory(parentReference);
        foreach (var row in RowsOf(reference))
        {
            count++;
            only = row;
            if (parent != NoRow && _parent[row] == parent && string.Equals(_names.Text(_name[row]), name, StringComparison.OrdinalIgnoreCase))
                return row;
        }
        return count == 1 ? only : NoRow;
    }

    private IEnumerable<uint> StaleRowsOf(ulong reference)
    {
        var record = RecordOf(reference);
        if (record >= _rowByRecord.Length) yield break;
        var first = _rowByRecord[record] - 1;
        if (first < 0) yield break;
        for (var row = (uint)first; ; )
        {
            if (_frn[row] != reference && IsAlive(row)) yield return row;
            if (!_nextLink.TryGetValue(row, out var next)) yield break;
            row = next;
        }
    }

    private byte[] RowScratch()
    {
        if (_rowScratch.Length < _count) _rowScratch = new byte[_count + Math.Max(1024, _count / 64)];
        else Array.Clear(_rowScratch, 0, _count);
        return _rowScratch;
    }

    private List<uint> Descendants(HashSet<uint> folders)
    {
        var state = RowScratch();
        foreach (var folder in folders) state[folder] = 2;
        state[_root] = state[_root] == 2 ? (byte)2 : (byte)3;
        var result = new List<uint>();
        var chain = new List<uint>();
        for (var row = 0u; row < _count; row++)
        {
            if (state[row] != 0 || !IsAlive(row) && !folders.Contains(row)) continue;
            chain.Clear();
            var at = row;
            byte verdict = 3;
            while (at != NoRow)
            {
                if (state[at] != 0) { verdict = state[at]; break; }
                chain.Add(at);
                at = _parent[at];
            }
            foreach (var member in chain)
            {
                state[member] = verdict;
                if (verdict == 2 && IsAlive(member)) result.Add(member);
            }
        }
        return result;
    }

    private void UpdateOrders(HashSet<uint> touched)
    {
        if (touched.Count == 0) return;
        var words = (_count + 63) >> 6;
        if (_bitScratch.Length < words) _bitScratch = new ulong[words + Math.Max(16, words / 64)];
        var removed = _bitScratch;
        foreach (var row in touched) removed[row >> 6] |= 1UL << (int)(row & 63);
        try
        {
            var alive = touched.Where(IsAlive).ToArray();
            MergeOrder(_byName, removed, true, (uint[])alive.Clone(), CompareByName);
            var files = alive.Where(row => !IsDirectory(row)).ToArray();
            if (_bySize is { } bySize) MergeOrder(bySize, removed, true, (uint[])files.Clone(), CompareBySize);
            if (_byModified is { } byModified) MergeOrder(byModified, removed, true, (uint[])files.Clone(), CompareByModified);
            if (_byKind is { } byKind) MergeOrder(byKind, removed, true, (uint[])alive.Clone(), CompareByKind);
        }
        finally
        {
            foreach (var row in touched) removed[row >> 6] = 0;
        }
    }
}
