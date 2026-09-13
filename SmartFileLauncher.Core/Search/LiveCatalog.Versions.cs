namespace SmartFileLauncher.Core.Search;

internal sealed partial class LiveCatalog
{
    internal sealed record PageArea(string Name, int Length, LivePages.PageReference[] Pages);
    internal sealed record Frame(int Version, string Root, long IndexedUtc, LiveReadStamp Stamp, int DirectoryCount,
        int NodeBase, int NodeSplit, int TermBase, int TermSplit, long Sequence, string? DeliveryId, PageArea[] Areas, string Contract = "live-tr-nfc-fold-1", int DeletedNodes = 0, string[]? PendingRepairs = null);
    internal int DirectoryCount => _directoryCount;
    internal string RootPath => _root;
    internal string StorageDirectoryName => Path.GetFileName(_directory);
    internal PackedRecord? FindCurrentRecord(string path) => Read(() => FindPath(path) is var id && id != 0 ? ReadRecord(id, _stamp) : null);
    private LiveCatalog(LiveCatalog source)
    {
        _directory = source._directory; _root = source._root; _rootPrefix = source._rootPrefix; _indexedUtc = source._indexedUtc;
        _areas = source._areas.Select(area => area.Fork(true)).ToArray();
        (_nodes, _names, _metadata, _terms, _postings, _nodeBuckets, _termBuckets, _nodeState, _termState) =
            (_areas[0], _areas[1], _areas[2], _areas[3], _areas[4], _areas[5], _areas[6], _areas[7], _areas[8]);
        _metadataWriter = new(_metadata, System.Text.Encoding.UTF8, true);
        _nodeCount = source._nodeCount; _itemCount = source._itemCount; _termCount = source._termCount;
        _maxTerm = source._maxTerm; _readyItems = source._readyItems; _activeTerms = source._activeTerms; _directoryCount = source._directoryCount;
        _nodeBase = source._nodeBase; _nodeSplit = source._nodeSplit; _termBase = source._termBase; _termSplit = source._termSplit;
        _stamp = source._stamp;
        _deletedNodes = source._deletedNodes;
    }
    private LiveCatalog(string directory, Frame frame)
    {
        if (frame.Version != 2 || frame.Contract != "live-tr-nfc-fold-1" || frame.Areas.Length != AreaNames.Length || !frame.Areas.Select(area => area.Name).SequenceEqual(AreaNames))
            throw new InvalidDataException("Live sürüm sözleşmesi uyuşmuyor.");
        _directory = directory; _root = frame.Root; _rootPrefix = _root.EndsWith('\\') ? _root : _root + "\\"; _indexedUtc = frame.IndexedUtc;
        var areas = new List<LivePages>();
        try { foreach (var area in frame.Areas) areas.Add(new LivePages(directory, area.Name, area.Length, area.Pages)); }
        catch { foreach (var area in areas) area.Dispose(); throw; }
        _areas = areas.ToArray();
        (_nodes, _names, _metadata, _terms, _postings, _nodeBuckets, _termBuckets, _nodeState, _termState) =
            (_areas[0], _areas[1], _areas[2], _areas[3], _areas[4], _areas[5], _areas[6], _areas[7], _areas[8]);
        _metadataWriter = new(Stream.Null); _stamp = frame.Stamp;
        _nodeCount = _stamp.Nodes; _itemCount = _stamp.Items; _termCount = _stamp.Terms; _maxTerm = _stamp.MaxTerm;
        _readyItems = _stamp.ReadyItems; _activeTerms = _stamp.ActiveTerms; _directoryCount = frame.DirectoryCount;
        _deletedNodes = frame.DeletedNodes;
        _nodeBase = frame.NodeBase; _nodeSplit = frame.NodeSplit; _termBase = frame.TermBase; _termSplit = frame.TermSplit; _sealed = true;
        if (_nodeCount - _deletedNodes != _itemCount || _deletedNodes < 0 || _itemCount != _readyItems || _activeTerms < 0 || _activeTerms > _termCount ||
            _directoryCount < 0 || _directoryCount > _itemCount || _nodes.Used != checked(_nodeCount * NodeSize) ||
            _terms.Used != checked(_termCount * TermSize) || _names.Used != _stamp.NamesEnd || _metadata.Used != _stamp.MetadataEnd ||
            _nodeBuckets.Used != checked((_nodeBase + _nodeSplit) * 4) || _termBuckets.Used != checked((_termBase + _termSplit) * 4))
        { Dispose(); throw new InvalidDataException("Live sürüm sayıları uyuşmuyor."); }
    }
    internal static LiveCatalog OpenFrame(string directory, Frame frame) => new(directory, frame);
    internal LiveCatalog ForkForUpdate()
    {
        ObjectDisposedException.ThrowIf(_disposeRequested, this);
        if (!_sealed) throw new InvalidOperationException("Live güncellemesi tamamlanmış katalog gerektirir.");
        return new(this);
    }
    internal Frame FinishFrame(long sequence, string? deliveryId)
    {
        if (_poisoned || _readyItems != _itemCount || _nodeCount - _deletedNodes != _itemCount) throw new InvalidDataException("Live işleminde eksik ebeveyn var.");
        Publish();
        foreach (var area in _areas) area.FlushDurable();
        var frame = new Frame(2, _root, _indexedUtc, _stamp, _directoryCount, _nodeBase, _nodeSplit, _termBase, _termSplit,
            sequence, deliveryId, _areas.Select((area, index) => new PageArea(AreaNames[index], area.Used, area.References())).ToArray(), DeletedNodes: _deletedNodes);
        _sealed = true; _nameOffsets = null; _nameCollisions = null; return frame;
    }
    internal void RetireReplacedBy(LiveCatalog next)
    { for (var index = 0; index < _areas.Length; index++) _areas[index].RetireReplacedPages(next._areas[index]); }
    internal void AbandonUpdate() { foreach (var area in _areas) area.AbandonPrivatePages(); Dispose(); }
}
