using System.Buffers;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Search;

internal sealed partial class LiveCatalog
{
    private T Read<T>(Func<T> read)
    {
        _gate.EnterReadLock();
        try { ObjectDisposedException.ThrowIf(_disposed, this); return read(); }
        finally { _gate.ExitReadLock(); }
    }
    private int FindPath(string path)
    {
        if (string.Equals(path, _root, StringComparison.OrdinalIgnoreCase)) return NodeDeleted(1) ? 0 : 1;
        if (!path.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase)) return 0;
        var id = 1; var rest = path.AsSpan(_rootPrefix.Length);
        while (!rest.IsEmpty)
        {
            var slash = rest.IndexOf('\\'); var part = slash < 0 ? rest : rest[..slash];
            id = FindNode(id, part); if (id == 0) return 0;
            if (slash < 0) return id;
            rest = rest[(slash + 1)..];
        }
        return 0;
    }
    private string? PathAt(int id, LiveReadStamp stamp, Dictionary<int, string>? cache = null)
    {
        if (id == 1) return _root;
        if (cache is not null && cache.TryGetValue(id, out var saved)) return saved;
        var length = _root.TrimEnd('\\').Length; var node = id;
        for (var steps = 0; node != 1; steps++)
        {
            if (node <= 0 || node > stamp.Nodes) return null;
            if (steps > stamp.Nodes) throw new InvalidDataException("Live ebeveyn döngüsü.");
            if ((_nodes.Int32(Row(node)) & LateName) != 0 && !Arrived(node, stamp)) return null;
            var name = _nodes.Int32(Row(node) + 4);
            if (name == 0 || name >= stamp.NamesEnd) return null;
            length = checked(length + 1 + _names.TextLength(name)); node = Parent(node);
        }
        var path = string.Create(length, (Owner: this, Id: id), static (span, state) =>
        {
            var current = state.Id; var position = span.Length;
            while (current != 1)
            {
                var name = state.Owner._nodes.Int32(state.Owner.Row(current) + 4);
                var count = state.Owner._names.TextLength(name); position -= count;
                state.Owner._names.DecodeText(name, span.Slice(position, count)); span[--position] = '\\'; current = state.Owner.Parent(current);
            }
            state.Owner._root.AsSpan().TrimEnd('\\').CopyTo(span[..position]);
        });
        if (cache is not null) cache[id] = path;
        return path;
    }
    private readonly record struct LiveMetadata(uint Flags, long ModifiedUtc, long CreatedUtc, long? Size,
        int OpenCount, DateTime? Created, DateTime? Modified, long IndexedUtc, int Extra);
    private LiveMetadata ReadMetadata(int id)
    {
        var offset = MetadataAt(id) + (DirectoryNode(id) ? 4 : 0);
        var flags = checked((uint)_metadata.Unsigned(ref offset, _metadata.Used));
        var modifiedUtc = _metadata.Int64(offset); offset += 8;
        var createdUtc = (flags & PackedFormat.RawCreated) != 0 ? checked(modifiedUtc + PackedFormat.DecodeSigned(_metadata.Unsigned(ref offset, _metadata.Used))) : 0;
        long? size = (flags & 2) != 0 ? PackedFormat.DecodeSigned(_metadata.Unsigned(ref offset, _metadata.Used)) : null;
        var open = (flags & PackedFormat.OpenCount) != 0 ? checked((int)PackedFormat.DecodeSigned(_metadata.Unsigned(ref offset, _metadata.Used))) : 0;
        DateTime? created = (flags & 4) != 0 ? PackedFormat.RestoreDate(createdUtc, (DateTimeKind)((flags >> 4) & 3)) : null;
        DateTime? modified = (flags & 8) != 0 ? PackedFormat.RestoreDate(modifiedUtc, (DateTimeKind)((flags >> 6) & 3)) : null;
        if ((flags & PackedFormat.CreatedOverride) != 0) { created = new(_metadata.Int64(offset), (DateTimeKind)((flags >> 4) & 3)); offset += 8; }
        if ((flags & PackedFormat.ModifiedOverride) != 0) { modified = new(_metadata.Int64(offset), (DateTimeKind)((flags >> 6) & 3)); offset += 8; }
        var indexed = (flags & PackedFormat.IndexedOverride) != 0 ? checked(_indexedUtc + PackedFormat.DecodeSigned(_metadata.Unsigned(ref offset, _metadata.Used))) : _indexedUtc;
        return new(flags, modifiedUtc, createdUtc, size, open, created, modified, indexed, offset);
    }
    private PackedRecord? ReadRecord(int id, LiveReadStamp stamp, Dictionary<int, string>? paths = null)
    {
        var item = ReadItem(id, stamp, paths, out var meta);
        return item is null ? null : new(item, meta.ModifiedUtc, meta.CreatedUtc, (meta.Flags & PackedFormat.Hidden) != 0,
            (meta.Flags & PackedFormat.System) != 0, meta.IndexedUtc);
    }
    private SearchItem? ReadItem(int id, LiveReadStamp stamp, Dictionary<int, string>? paths, out LiveMetadata meta)
    {
        meta = default;
        if (!Arrived(id, stamp)) return null;
        meta = ReadMetadata(id); var extra = meta.Extra;
        string name; string? path, parent;
        if ((meta.Flags & PackedFormat.ComplexPath) != 0)
        { name = _metadata.Text(ref extra, stamp.MetadataEnd)!; path = _metadata.Text(ref extra, stamp.MetadataEnd); parent = _metadata.Text(ref extra, stamp.MetadataEnd); }
        else
        {
            name = Name(id)!;
            parent = id == 1 ? "" : PathAt(Parent(id), stamp, paths);
            if (parent is null) return null;
            path = id == 1 ? _root : parent.TrimEnd('\\') + "\\" + name;
        }
        if (path is null) return null;
        return new(name, path, (meta.Flags & 1) != 0, meta.Size, meta.Created, meta.Modified, meta.OpenCount, parent);
    }
    private IEnumerable<int> ChildIds(int id)
    {
        if (!DirectoryNode(id)) yield break;
        var count = 0;
        for (var child = _metadata.Int32(MetadataAt(id)); child != 0; child = _nodes.Int32(Row(child) + 12))
        { if (++count > _nodeCount) throw new InvalidDataException("Live çocuk döngüsü."); yield return child; }
    }
    private static bool MatchesTerm(string source, char[] target, int length, int? distance, int[]? scratch)
    {
        if (distance is not int max) return MemoryExtensions.Contains(target.AsSpan(0, length), source.AsSpan(), StringComparison.OrdinalIgnoreCase);
        if (source.Length == 0 || length == 0 || max < 0 || Math.Abs(source.Length - length) > max) return false;
        var width = length + 1; var previous = scratch.AsSpan(0, width); var current = scratch.AsSpan(width, width);
        for (var j = 0; j <= length; j++) previous[j] = j;
        for (var i = 1; i <= source.Length; i++)
        {
            current[0] = i; var minimum = i;
            for (var j = 1; j <= length; j++)
            { current[j] = Math.Min(Math.Min(previous[j] + 1, current[j - 1] + 1), previous[j - 1] + (source[i - 1] == target[j - 1] ? 0 : 1)); minimum = Math.Min(minimum, current[j]); }
            if (minimum > max) return false;
            var swap = previous; previous = current; current = swap;
        }
        return previous[length] <= max;
    }
    private List<SearchItem> Matching(LiveReadStamp stamp, string token, int? distance, bool exact, CancellationToken ct)
    {
        var results = new List<SearchItem>(); var paths = new Dictionary<int, string>();
        if (exact)
        {
            foreach (var id in Postings(FindTerm(token), stamp))
            { ct.ThrowIfCancellationRequested(); if (ReadItem(id, stamp, paths, out _) is { } item) results.Add(item); }
            return results;
        }
        var words = checked((stamp.Nodes + 31) / 32); var seen = ArrayPool<uint>.Shared.Rent(Math.Max(words, 1)); Array.Clear(seen, 0, words);
        try
        {
            foreach (var term in MatchingTermIds(stamp, token, distance, ct))
            {
                foreach (var id in Postings(term, stamp))
                {
                    ct.ThrowIfCancellationRequested();
                    var index = id - 1; var mask = 1u << (index & 31);
                    if ((seen[index >> 5] & mask) != 0) continue;
                    seen[index >> 5] |= mask;
                    if (ReadItem(id, stamp, paths, out _) is { } item) results.Add(item);
                }
            }
        }
        finally { ArrayPool<uint>.Shared.Return(seen); }
        return results;
    }
    private IEnumerable<int> MatchingTermIds(LiveReadStamp stamp, string token, int? distance, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (distance is int limit && (limit < 0 || token.Length == 0)) yield break;
        var ascii = token.All(static ch => ch <= 127) ? System.Text.Encoding.ASCII.GetBytes(token) : null;
        var buffer = ArrayPool<char>.Shared.Rent(Math.Max(stamp.MaxTerm, 1));
        var scratch = distance.HasValue ? ArrayPool<int>.Shared.Rent(2 * (stamp.MaxTerm + 1)) : null;
        try
        {
            for (var term = 1; term <= stamp.Terms; term++)
            {
                ct.ThrowIfCancellationRequested();
                var offset = _terms.Int32(Term(term));
                if (ascii is not null && _names.TryMatchAscii(offset, ascii, distance, scratch, out var matches))
                {
                    if (matches) yield return term;
                    continue;
                }
                if (distance is int maximum && Math.Abs(_names.TextLength(offset) - token.Length) > maximum) continue;
                var length = _names.DecodeText(offset, buffer);
                if (MatchesTerm(token, buffer, length, distance, scratch)) yield return term;
            }
        }
        finally { ArrayPool<char>.Shared.Return(buffer); if (scratch is not null) ArrayPool<int>.Shared.Return(scratch); }
    }

    internal sealed class LiveCatalogSnapshot : IQueryCatalogSnapshot, IDisposable
    {
        private readonly LiveCatalog _owner;
        private readonly LiveReadStamp _stamp;
        private int _released;
        internal LiveCatalogSnapshot(LiveCatalog owner, LiveReadStamp stamp) { _owner = owner; _stamp = stamp; }
        public void Dispose() { if (Interlocked.Exchange(ref _released, 1) == 0) _owner.ReleaseReader(); GC.SuppressFinalize(this); }
        ~LiveCatalogSnapshot() { Dispose(); }
        public int ItemCount => _stamp.ReadyItems;
        internal int AcceptedItemCount => _stamp.Items;
        internal int PendingItemCount => _stamp.Items - _stamp.ReadyItems;
        public int TokenCount => _stamp.ActiveTerms < 0 ? _stamp.Terms : _stamp.ActiveTerms;
        internal int StructuralNodeCount => _stamp.Nodes;
        public ISearchStateReader ForQuery() => this;
        internal PackedRecord? GetRecord(int id) => _owner.Read(() => id <= 0 || id > _stamp.Nodes ? null : _owner.ReadRecord(id, _stamp));
        internal PackedRecord? FindRecord(string path) => _owner.Read(() => _owner.FindPath(path) is var id && id != 0 ? _owner.ReadRecord(id, _stamp) : null);
        public bool TryGetItem(string path, out SearchItem item)
        { var record = FindRecord(path); item = record?.Item!; return record is not null; }
        public bool ContainsPath(string path) => FindRecord(path) is not null;
        public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken cancellationToken = default) =>
            _owner.Read(() => _owner.Matching(_stamp, token, null, true, cancellationToken));
        public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken cancellationToken = default) =>
            _owner.Read(() => _owner.Matching(_stamp, token, null, false, cancellationToken));
        public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken cancellationToken = default) => _owner.Read(() =>
        {
            var exact = _owner.Matching(_stamp, token, null, true, cancellationToken);
            return exact.Count != 0 ? exact : _owner.Matching(_stamp, token, maxDistance, false, cancellationToken);
        });
        public IReadOnlyCollection<SearchItem> GetRoots(CancellationToken cancellationToken = default) =>
            GetRecord(1) is { } root ? new[] { root.Item } : Array.Empty<SearchItem>();
        public IReadOnlyCollection<SearchItem> GetChildren(string parentPath, CancellationToken cancellationToken = default) => _owner.Read(() =>
        {
            var results = new List<SearchItem>(); var id = _owner.FindPath(parentPath);
            if (id == 0) return results;
            foreach (var child in _owner.ChildIds(id))
            { cancellationToken.ThrowIfCancellationRequested(); if (_owner.ReadRecord(child, _stamp) is { } record) results.Add(record.Item); }
            return results;
        });
        public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken cancellationToken = default) => _owner.Read(() =>
        {
            var results = new List<SearchItem>(); var queue = new Queue<int>(); var root = _owner.FindPath(item.FullPath);
            if (root == 0) return results;
            queue.Enqueue(root);
            while (queue.TryDequeue(out var id))
                foreach (var child in _owner.ChildIds(id))
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    if (child > _stamp.Nodes) continue;
                    if (_owner.ReadRecord(child, _stamp) is { } record) results.Add(record.Item);
                    if (_owner.DirectoryNode(child)) queue.Enqueue(child);
                }
            return results;
        });
        public IEnumerable<SearchItem> GetItems(QueryCatalogFilter filter, CancellationToken cancellationToken = default)
        {
            var paths = new Dictionary<int, string>();
            for (var start = 1; start <= _stamp.Nodes; start += 1024)
            {
                var first = start;
                var batch = _owner.Read(() =>
                {
                    var result = new List<SearchItem>();
                    for (var id = first; id < first + 1024 && id <= _stamp.Nodes; id++)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        if (!_owner.Arrived(id, _stamp)) continue;
                        var meta = _owner.ReadMetadata(id);
                        if (filter.Matches((meta.Flags & 1) != 0, meta.Size, meta.Created, meta.Modified) && _owner.ReadRecord(id, _stamp, paths) is { } record) result.Add(record.Item);
                    }
                    return result;
                });
                foreach (var item in batch) yield return item;
            }
        }
        public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken cancellationToken = default) =>
            GetItems(new(true, true, false, false, null, null, null, null, null, null), cancellationToken).ToArray();
        internal IReadOnlyList<LivePendingMatch> GetPending(string token) => _owner.Read(() =>
            _owner.Postings(_owner.FindTerm(token), _stamp).Where(id => _owner.PathAt(id, _stamp) is null)
                .Select(id => new LivePendingMatch(id, _owner.Name(id)!, _owner.Parent(id))).ToArray());
        public ISearchStateReader WithUpserts(IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer) => throw new NotSupportedException("Live ilk edinim LiveCatalog.Add üzerinden yazılır.");
        public ISearchStateReader WithoutPathAndDescendants(string path) => throw new NotSupportedException("Live ilk edinim tamamlanmış nesli değiştirmez.");
        public ISearchStateReader WithChanges(IEnumerable<string> removedPaths, IEnumerable<FileSystemNode> upserts, ITokenizer tokenizer) => throw new NotSupportedException("Live ilk edinim tamamlanmış nesli değiştirmez.");
    }
}
