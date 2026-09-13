using System.Buffers;
using System.Collections.Immutable;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SmartFileLauncher.Core.Indexing.Ntfs;

namespace SmartFileLauncher.Core.Search;

internal readonly record struct LiveReadStamp(int Nodes, int Items, int Terms, int NamesEnd, int MetadataEnd, int MaxTerm, int ReadyItems = 0, int ActiveTerms = -1);
internal sealed record LivePendingMatch(int Id, string Name, int ParentId);
internal enum LiveBuildStage { Begin, Placement, Metadata, Tokens, Published }

internal sealed partial class LiveCatalog : IDisposable
{
    private const int NodeSize = 20, TermSize = 24, PostingBlock = 32;
    private readonly string _directory, _root, _rootPrefix;
    private readonly long _indexedUtc;
    private readonly ReaderWriterLockSlim _gate = new();
    private readonly LivePages _nodes, _names, _metadata, _terms, _postings, _nodeBuckets, _termBuckets, _nodeState, _termState;
    private readonly LivePages[] _areas;
    private readonly BinaryWriter _metadataWriter;
    private readonly BasicTokenizer _tokenizer = new();
    private int _nodeCount, _itemCount, _termCount, _maxTerm;
    private int _readyItems;
    private int _activeTerms;
    private int _directoryCount;
    private int _deletedNodes;
    private const int Connected = 1 << 30, LateName = 1 << 29, ParentMask = LateName - 1;
    private Dictionary<ulong, int>? _nameOffsets;
    private Dictionary<ulong, List<int>>? _nameCollisions;
    private int _nodeBase = 16, _nodeSplit, _termBase = 16, _termSplit;
    private LiveReadStamp _stamp;
    private bool _sealed, _disposed, _poisoned;
    private bool _disposeRequested;
    private int _readerLeases;
    internal Action? BeforePublish { get; set; }
    internal Action<LiveBuildStage>? AllocationCheckpoint { get; set; }
    private sealed record AreaState(string Name, int Length, string Hash);
    private sealed record Manifest(int Version, string Contract, string Root, long IndexedUtc, LiveReadStamp Stamp,
        int NodeBase, int NodeSplit, int TermBase, int TermSplit, AreaState[] Areas, int DirectoryCount = 0);
    private static readonly string[] AreaNames = ["nodes", "names", "metadata", "terms", "postings", "node-buckets", "term-buckets", "node-state", "term-state"];

    internal LiveCatalog(string directory, string root, long? indexedUtc = null)
    {
        _directory = Path.GetFullPath(directory); _root = Path.TrimEndingDirectorySeparator(Path.GetFullPath(root));
        _rootPrefix = _root.EndsWith('\\') ? _root : _root + "\\";
        _indexedUtc = indexedUtc ?? DateTime.UtcNow.Ticks;
        _nameOffsets = new();
        if (Directory.Exists(_directory)) throw new ArgumentException("Yeni live katalog dizini gerekli.");
        Directory.CreateDirectory(_directory);
        _areas = AreaNames.Select(name => new LivePages(_directory, name)).ToArray();
        (_nodes, _names, _metadata, _terms, _postings, _nodeBuckets, _termBuckets) = (_areas[0], _areas[1], _areas[2], _areas[3], _areas[4], _areas[5], _areas[6]);
        (_nodeState, _termState) = (_areas[7], _areas[8]);
        _metadataWriter = new BinaryWriter(_metadata, Encoding.UTF8, true);
        _names.Allocate(1); _metadata.Allocate(8); _postings.Allocate(8);
        _nodeBuckets.Allocate(_nodeBase * 4); _termBuckets.Allocate(_termBase * 4);
        var rootName = Path.GetFileName(_root);
        NewNode(0, rootName.Length == 0 ? _root : rootName, true);
        Publish();
    }

    private LiveCatalog(string directory, Manifest manifest)
    {
        _directory = directory; _root = manifest.Root; _indexedUtc = manifest.IndexedUtc;
        _rootPrefix = _root.EndsWith('\\') ? _root : _root + "\\";
        _areas = manifest.Areas.Select(area => new LivePages(directory, area.Name, true, area.Length)).ToArray();
        (_nodes, _names, _metadata, _terms, _postings, _nodeBuckets, _termBuckets) = (_areas[0], _areas[1], _areas[2], _areas[3], _areas[4], _areas[5], _areas[6]);
        (_nodeState, _termState) = (_areas[7], _areas[8]);
        _metadataWriter = new BinaryWriter(Stream.Null);
        _nodeCount = manifest.Stamp.Nodes; _itemCount = manifest.Stamp.Items; _termCount = manifest.Stamp.Terms; _maxTerm = manifest.Stamp.MaxTerm;
        _readyItems = manifest.Stamp.ReadyItems;
        _activeTerms = manifest.Stamp.ActiveTerms < 0 ? manifest.Stamp.Terms : manifest.Stamp.ActiveTerms;
        _directoryCount = manifest.DirectoryCount;
        _nodeBase = manifest.NodeBase; _nodeSplit = manifest.NodeSplit; _termBase = manifest.TermBase; _termSplit = manifest.TermSplit;
        _stamp = manifest.Stamp; _sealed = true;
        try
        {
            if (_nodes.Used != checked(_nodeCount * NodeSize) || _terms.Used != checked(_termCount * TermSize) ||
                _names.Used != _stamp.NamesEnd || _metadata.Used != _stamp.MetadataEnd || _itemCount < 0 || _itemCount != _nodeCount || _readyItems != _itemCount ||
                _nodeBuckets.Used != checked((_nodeBase + _nodeSplit) * 4) || _termBuckets.Used != checked((_termBase + _termSplit) * 4))
                throw new InvalidDataException("Live manifest alanları uyuşmuyor.");
            for (var i = 0; i < _areas.Length; i++)
                if (_areas[i].Hash() != manifest.Areas[i].Hash) throw new InvalidDataException("Live alan checksum uyuşmuyor.");
        }
        catch { foreach (var area in _areas) area.Dispose(); throw; }
    }

    internal static LiveCatalog Open(string directory)
    {
        directory = Path.GetFullPath(directory);
        if (File.Exists(Path.Combine(directory, "head.bin"))) return OpenFrame(directory, LiveCatalogStore.ReadHead(directory));
        var bytes = File.ReadAllBytes(Path.Combine(directory, "manifest.bin"));
        if (bytes.Length < 32 || bytes.Length > 65536 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)), bytes.AsSpan(bytes.Length - 32)))
            throw new InvalidDataException("Live manifest checksum uyuşmuyor.");
        var manifest = JsonSerializer.Deserialize<Manifest>(bytes.AsSpan(0, bytes.Length - 32)) ?? throw new InvalidDataException("Live manifest yok.");
        if (manifest.Version != 1 || manifest.Contract != PackedCheckpoint.CurrentContract ||
            manifest.Areas.Length != AreaNames.Length || !manifest.Areas.Select(area => area.Name).SequenceEqual(AreaNames))
            throw new InvalidDataException("Live sözleşme uyuşmuyor.");
        return new(directory, manifest);
    }

    internal LiveCatalogSnapshot Snapshot()
    {
        _gate.EnterWriteLock();
        try { ObjectDisposedException.ThrowIf(_disposeRequested, this); _readerLeases++; return new(this, _stamp); }
        finally { _gate.ExitWriteLock(); }
    }
    internal int Add(PackedRecord record)
    {
        _gate.EnterWriteLock();
        try
        {
            CheckWritable();
            AllocationCheckpoint?.Invoke(LiveBuildStage.Begin);
            var id = EnsurePath(record.Item.FullPath, record.Item.IsDirectory, out var lexicalDifference);
            if (Arrived(id, _stamp))
            {
                if (ReadRecord(id, _stamp) != Normalize(record)) throw new InvalidDataException("Live ilk edinimde çelişen aynı kayıt.");
                return id;
            }
            if (NodeDeleted(id)) { SetPackedState(_nodeState, id, 2); _deletedNodes--; }
            var canonicalParent = id == 1 ? ReadOnlySpan<char>.Empty : Parent(id) == 1 ? _root.AsSpan()
                : record.Item.FullPath.AsSpan(0, record.Item.FullPath.LastIndexOf('\\'));
            var complex = lexicalDifference || !NameEquals(_nodes.Int32(Row(id) + 4), record.Item.Name, StringComparison.Ordinal) ||
                record.Item.ParentPath is null || !record.Item.ParentPath.AsSpan().SequenceEqual(canonicalParent);
            AllocationCheckpoint?.Invoke(LiveBuildStage.Placement);
            Fill(id, record, complex);
            BeforePublish?.Invoke(); Publish(); AllocationCheckpoint?.Invoke(LiveBuildStage.Published); return id;
        }
        catch { _poisoned = true; throw; }
        finally { _gate.ExitWriteLock(); }
    }
    private PackedRecord Normalize(PackedRecord record) => record.IndexedUtc == 0 ? record with { IndexedUtc = _indexedUtc } : record;
    private int Row(int id) => id > 0 && id <= _nodeCount ? (id - 1) * NodeSize : throw new InvalidDataException("Live node ID geçersiz.");
    private int Term(int id) => id > 0 && id <= _termCount ? (id - 1) * TermSize : throw new InvalidDataException("Live term ID geçersiz.");
    private int Parent(int id) => _nodes.Int32(Row(id)) & ParentMask;
    private bool IsConnected(int id) => (_nodes.Int32(Row(id)) & Connected) != 0;
    private bool DirectoryNode(int id) => _nodes.Int32(Row(id)) < 0;
    private int MetadataAt(int id) => _nodes.Int32(Row(id) + 8) & int.MaxValue;
    private bool Arrived(int id, LiveReadStamp stamp) => id > 0 && id <= stamp.Nodes && !NodeDeleted(id) && _nodes.Int32(Row(id) + 8) < 0 && MetadataAt(id) < stamp.MetadataEnd;
    private string? Name(int id)
    { var offset = _nodes.Int32(Row(id) + 4); return offset == 0 ? null : _names.Text(ref offset, _names.Used); }
    private int AddName(ReadOnlySpan<char> name)
    {
        _nameOffsets ??= new();
        var hash = 14695981039346656037UL;
        foreach (var ch in name) hash = unchecked((hash ^ ch) * 1099511628211UL);
        var found = _nameOffsets!.TryGetValue(hash, out var existing);
        if (found && NameEquals(existing, name, StringComparison.Ordinal)) return existing;
        if (found && _nameCollisions is not null && _nameCollisions.TryGetValue(hash, out var collisions))
            foreach (var candidate in collisions)
                if (NameEquals(candidate, name, StringComparison.Ordinal)) return candidate;
        var offset = _names.Used; _names.Position = offset; _names.WriteText(name);
        if (!found) _nameOffsets.Add(hash, offset);
        else
        {
            _nameCollisions ??= new();
            if (!_nameCollisions.TryGetValue(hash, out var entries)) _nameCollisions.Add(hash, entries = []);
            entries.Add(offset);
        }
        return offset;
    }
    private int NewNode(int parent, string? name, bool directory) => NewNode(parent, name.AsSpan(), directory, name is not null);
    private int NewNode(int parent, ReadOnlySpan<char> name, bool directory, bool hasName = true)
    {
        var id = ++_nodeCount; var row = _nodes.Allocate(NodeSize);
        var connected = hasName && (id == 1 || parent != 0 && IsConnected(parent));
        _nodes.PutInt32(row, parent | (directory ? int.MinValue : 0) | (connected ? Connected : 0) | (!hasName ? LateName : 0));
        if (hasName) _nodes.PutInt32(row + 4, AddName(name));
        if (directory) _nodes.PutInt32(row + 8, _metadata.Allocate(4));
        if (parent != 0) LinkChild(parent, id);
        if (hasName) InsertNodeHash(id, Hash(parent, name));
        return id;
    }
    private void LinkChild(int parent, int child)
    {
        if (!DirectoryNode(parent)) throw new InvalidDataException("Live ebeveyn dizin değil.");
        var head = MetadataAt(parent);
        _nodes.PutInt32(Row(child) + 12, _metadata.Int32(head)); _metadata.PutInt32(head, child);
    }
    private int EnsurePath(string path, bool directory, out bool lexicalDifference)
    {
        if (string.Equals(path, _root, StringComparison.OrdinalIgnoreCase)) { lexicalDifference = path != _root; return 1; }
        if (!path.StartsWith(_rootPrefix, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Live yol kök dışında.");
        lexicalDifference = !path.AsSpan(0, _rootPrefix.Length).SequenceEqual(_rootPrefix);
        var parent = 1; var rest = path.AsSpan(_rootPrefix.Length);
        while (!rest.IsEmpty)
        {
            var slash = rest.IndexOf('\\'); var segment = slash < 0 ? rest : rest[..slash];
            if (segment.IsEmpty) throw new InvalidDataException("Live boş yol parçası.");
            var id = FindNode(parent, segment);
            if (id == 0) id = NewNode(parent, segment, slash >= 0 || directory);
            else if (!lexicalDifference && !NameEquals(_nodes.Int32(Row(id) + 4), segment, StringComparison.Ordinal)) lexicalDifference = true;
            if (DirectoryNode(id) != (slash >= 0 || directory)) throw new InvalidDataException("Live dosya/dizin çakışması.");
            parent = id;
            if (slash < 0) break;
            rest = rest[(slash + 1)..];
        }
        return parent;
    }
    private void Fill(int id, PackedRecord record, bool complex)
    {
        if (DirectoryNode(id) != record.Item.IsDirectory) throw new InvalidDataException("Live tür uyuşmuyor.");
        var oldHead = record.Item.IsDirectory ? _metadata.Int32(MetadataAt(id)) : 0;
        var start = _metadata.Used; _metadata.Position = start;
        if (record.Item.IsDirectory) _metadataWriter.Write(oldHead);
        PackedFormat.WriteMetadata(_metadataWriter, record, complex, _indexedUtc);
        if (complex)
        {
            PackedFormat.WriteText(_metadata, record.Item.Name);
            PackedFormat.WriteText(_metadata, record.Item.FullPath);
            PackedFormat.WriteText(_metadata, record.Item.ParentPath);
        }
        AllocationCheckpoint?.Invoke(LiveBuildStage.Metadata);
        char[]? rented = null;
        Span<char> scratch = record.Item.Name.Length <= 256 ? stackalloc char[256]
            : (rented = ArrayPool<char>.Shared.Rent(record.Item.Name.Length));
        try
        {
            using var tokens = new BasicTokenEnumerator(record.Item.Name, scratch, _tokenizer);
            while (tokens.MoveNext()) AddPosting(GetOrAddTerm(tokens.Current), id);
        }
        finally { if (rented is not null) ArrayPool<char>.Shared.Return(rented); }
        AllocationCheckpoint?.Invoke(LiveBuildStage.Tokens);
        _nodes.PutInt32(Row(id) + 8, start | int.MinValue);
        _itemCount++;
        if (record.Item.IsDirectory) _directoryCount++;
        if (IsConnected(id)) _readyItems++;
    }
    private void ConnectSubtree(int id)
    {
        var queue = new Queue<int>(); queue.Enqueue(id);
        while (queue.TryDequeue(out var node))
        {
            if (IsConnected(node)) continue;
            var row = Row(node); _nodes.PutInt32(row, _nodes.Int32(row) | Connected);
            if (_nodes.Int32(row + 8) < 0) _readyItems++;
            foreach (var child in ChildIds(node)) queue.Enqueue(child);
        }
    }
    private void Publish() => _stamp = new(_nodeCount, _itemCount, _termCount, _names.Used, _metadata.Used, _maxTerm, _readyItems, _activeTerms);
    private void CheckWritable()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        if (_sealed || _poisoned) throw new InvalidOperationException("Live katalog yazmaya kapalı.");
    }
    internal void Seal()
    {
        _gate.EnterWriteLock();
        try
        {
            CheckWritable();
            if (_itemCount != _nodeCount || _readyItems != _itemCount) throw new InvalidDataException("Live envanterde metadata'sı veya yolu tamamlanmamış kayıtlar var.");
            foreach (var area in _areas) area.FlushDurable();
            var manifest = new Manifest(1, PackedCheckpoint.CurrentContract, _root, _indexedUtc, _stamp,
                _nodeBase, _nodeSplit, _termBase, _termSplit,
                _areas.Select((area, i) => new AreaState(AreaNames[i], area.Used, area.Hash())).ToArray(), _directoryCount);
            var bytes = JsonSerializer.SerializeToUtf8Bytes(manifest);
            var temp = Path.Combine(_directory, "manifest.next");
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Write(SHA256.HashData(bytes)); output.Flush(true); }
            File.Move(temp, Path.Combine(_directory, "manifest.bin"));
            _sealed = true;
            _nameOffsets = null; _nameCollisions = null;
        }
        finally { _gate.ExitWriteLock(); }
    }
    internal long UsedBytes => _areas.Sum(area => (long)area.Used);
    internal long AllocatedDiskBytes => _areas.Sum(area => area.DiskBytes);
    public void Dispose()
    {
        _gate.EnterWriteLock();
        try
        {
            if (_disposeRequested) return;
            _disposeRequested = true;
            if (_readerLeases == 0) ReleasePages();
        }
        finally { _gate.ExitWriteLock(); }
    }
    private void ReleasePages()
    {
        _disposed = true; _nameOffsets = null; _nameCollisions = null;
        _metadataWriter.Dispose(); foreach (var area in _areas) area.Dispose();
    }
    private void ReleaseReader()
    {
        _gate.EnterWriteLock();
        try { if (--_readerLeases == 0 && _disposeRequested) ReleasePages(); }
        finally { _gate.ExitWriteLock(); }
    }
}
