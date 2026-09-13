using System.Text;

namespace SmartFileLauncher.Core.Search;

internal sealed record LiveMutation(string Path, PackedRecord? Record, string? OldPath = null);

internal sealed partial class LiveCatalog
{
    private int PackedState(LivePages area, int id)
    {
        var index = (id - 1) >> 2;
        return index < area.Used ? (area.At(index) >> (((id - 1) & 3) * 2)) & 3 : 0;
    }
    private void SetPackedState(LivePages area, int id, int state)
    {
        var index = (id - 1) >> 2; var shift = ((id - 1) & 3) * 2;
        if (index >= area.Used) area.Allocate(index + 1 - area.Used);
        Span<byte> value = stackalloc byte[1]; value[0] = (byte)((area.At(index) & ~(3 << shift)) | state << shift);
        area.Put(index, value);
    }
    private bool NodeDeleted(int id) => (PackedState(_nodeState, id) & 1) != 0;
    private bool TermInactive(int id) => (PackedState(_termState, id) & 1) != 0;
    private bool PostingVisible(int term, int id, LiveReadStamp stamp)
    {
        if (!Arrived(id, stamp)) return false;
        if ((PackedState(_nodeState, id) & 2) == 0) return true;
        var termOffset = _terms.Int32(Term(term));
        var item = ReadItem(id, stamp, null, out _);
        return item is not null && _tokenizer.Tokenize(item.Name).Any(token => NameEquals(termOffset, token));
    }
    internal void ApplyMutation(LiveMutation mutation)
    {
        CheckWritable();
        if (mutation.Record is null) { DeletePath(mutation.Path); return; }
        var record = Normalize(mutation.Record);
        if (!string.Equals(record.Item.FullPath, mutation.Path, StringComparison.OrdinalIgnoreCase)) throw new InvalidDataException("Live işlem yolu uyuşmuyor.");
        var oldId = FindPath(mutation.OldPath ?? mutation.Path);
        if (oldId == 0 || !Arrived(oldId, _stamp)) { Add(record); return; }
        var previous = ReadRecord(oldId, _stamp)!;
        if (previous == record) return;
        if (previous.Item.IsDirectory != record.Item.IsDirectory)
        { DeletePath(previous.Item.FullPath); Add(record); return; }
        var renamed = previous.Item.Name != record.Item.Name || previous.Item.FullPath != record.Item.FullPath;
        var oldTokens = renamed ? _tokenizer.Tokenize(previous.Item.Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray() : [];
        if (renamed)
        {
            if (oldId == 1) throw new InvalidOperationException("İzlenen kökün taşınması yeni kök aboneliği gerektirir.");
            var parent = FindPath(record.Item.ParentPath ?? Path.GetDirectoryName(record.Item.FullPath)!);
            if (parent == 0 || !Arrived(parent, _stamp) || !DirectoryNode(parent)) throw new InvalidDataException("Live hedef ebeveyn eksik.");
            for (var ancestor = parent; ancestor != 0; ancestor = Parent(ancestor))
                if (ancestor == oldId) throw new InvalidDataException("Live taşıma ebeveyn döngüsü.");
            var collision = FindPath(record.Item.FullPath);
            if (collision != 0 && collision != oldId) DeletePath(record.Item.FullPath);
            UnlinkHash(oldId); UnlinkChild(oldId);
            var row = Row(oldId); _nodes.PutInt32(row, (_nodes.Int32(row) & ~ParentMask) | parent);
            _nodes.PutInt32(row + 4, AddName(Path.GetFileName(record.Item.FullPath)));
            LinkChild(parent, oldId); InsertNodeHash(oldId, StoredNameHash(parent, _nodes.Int32(row + 4)));
            SetPackedState(_nodeState, oldId, 2);
        }
        WriteUpdatedMetadata(oldId, record);
        Publish();
        if (renamed)
        {
            foreach (var token in _tokenizer.Tokenize(record.Item.Name).Distinct(StringComparer.OrdinalIgnoreCase))
            {
                var term = GetOrAddTerm(token); Publish();
                if (!Postings(term, _stamp).Contains(oldId)) AddPosting(term, oldId);
            }
            RefreshTerms(oldTokens);
            if (record.Item.IsDirectory) UpdateComplexDescendants(oldId, previous.Item.FullPath, record.Item.FullPath);
        }
        Publish();
    }
    private void WriteUpdatedMetadata(int id, PackedRecord record)
    {
        var previous = ReadMetadata(id); var end = previous.Extra;
        if ((previous.Flags & PackedFormat.ComplexPath) != 0)
            for (var field = 0; field < 3; field++) _metadata.TextRange(ref end, _metadata.Used);
        var oldOffset = MetadataAt(id); var capacity = end - oldOffset;
        var oldHead = DirectoryNode(id) ? _metadata.Int32(oldOffset) : 0;
        var canonicalParent = id == 1 ? "" : PathAt(Parent(id), _stamp);
        var canonicalPath = id == 1 ? _root : canonicalParent!.TrimEnd('\\') + "\\" + Name(id);
        var complex = record.Item.FullPath != canonicalPath || record.Item.ParentPath != canonicalParent || record.Item.Name != Name(id);
        using var memory = new MemoryStream();
        using (var writer = new BinaryWriter(memory, Encoding.UTF8, true))
        {
            if (record.Item.IsDirectory) writer.Write(oldHead);
            PackedFormat.WriteMetadata(writer, record, complex, _indexedUtc);
            if (complex) { PackedFormat.WriteText(memory, record.Item.Name); PackedFormat.WriteText(memory, record.Item.FullPath); PackedFormat.WriteText(memory, record.Item.ParentPath); }
        }
        var bytes = memory.GetBuffer().AsSpan(0, checked((int)memory.Length));
        var offset = bytes.Length <= capacity ? oldOffset : _metadata.Allocate(bytes.Length);
        _metadata.Put(offset, bytes); _nodes.PutInt32(Row(id) + 8, offset | int.MinValue);
    }
    private void DeletePath(string path)
    {
        var id = FindPath(path); if (id == 0) return;
        var queue = new Queue<int>(); queue.Enqueue(id); var terms = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        while (queue.TryDequeue(out var node))
        {
            if (NodeDeleted(node)) continue;
            if (DirectoryNode(node)) foreach (var child in ChildIds(node)) if (!NodeDeleted(child)) queue.Enqueue(child);
            if (Arrived(node, _stamp))
            {
                foreach (var token in _tokenizer.Tokenize(ReadItem(node, _stamp, null, out _)!.Name)) terms.Add(token);
                _itemCount--; _readyItems--; if (DirectoryNode(node)) _directoryCount--;
            }
            SetPackedState(_nodeState, node, PackedState(_nodeState, node) | 1); _deletedNodes++;
        }
        Publish(); RefreshTerms(terms); Publish();
    }
    private void RefreshTerms(IEnumerable<string> tokens)
    {
        foreach (var token in tokens)
        {
            var term = FindTerm(token);
            if (term != 0 && !TermInactive(term) && !Postings(term, _stamp).Any())
            { SetPackedState(_termState, term, 1); _activeTerms--; }
        }
    }
    private void UnlinkHash(int id)
    {
        var bucket = Bucket(StoredNameHash(Parent(id), _nodes.Int32(Row(id) + 4)), _nodeBase, _nodeSplit);
        var current = _nodeBuckets.Int32(bucket * 4); var previous = 0;
        while (current != 0 && current != id) { previous = current; current = _nodes.Int32(Row(current) + 16); }
        if (current == 0) throw new InvalidDataException("Live kayıt hash zincirinde yok.");
        var next = _nodes.Int32(Row(id) + 16);
        if (previous == 0) _nodeBuckets.PutInt32(bucket * 4, next); else _nodes.PutInt32(Row(previous) + 16, next);
    }
    private void UnlinkChild(int id)
    {
        var parent = Parent(id); var head = MetadataAt(parent); var current = _metadata.Int32(head); var previous = 0;
        while (current != 0 && current != id) { previous = current; current = _nodes.Int32(Row(current) + 12); }
        if (current == 0) throw new InvalidDataException("Live kayıt çocuk zincirinde yok.");
        var next = _nodes.Int32(Row(id) + 12);
        if (previous == 0) _metadata.PutInt32(head, next); else _nodes.PutInt32(Row(previous) + 12, next);
    }
    private void UpdateComplexDescendants(int root, string previousPath, string nextPath)
    {
        var queue = new Queue<int>(); queue.Enqueue(root);
        while (queue.TryDequeue(out var parent))
            foreach (var id in ChildIds(parent))
            {
                if (!Arrived(id, _stamp)) continue;
                if (DirectoryNode(id)) queue.Enqueue(id);
                if ((ReadMetadata(id).Flags & PackedFormat.ComplexPath) == 0) continue;
                var record = ReadRecord(id, _stamp)!;
                string? Move(string? path) => path is not null && path.StartsWith(previousPath + "\\", StringComparison.OrdinalIgnoreCase) ? nextPath + path[previousPath.Length..] : string.Equals(path, previousPath, StringComparison.OrdinalIgnoreCase) ? nextPath : path;
                WriteUpdatedMetadata(id, record with { Item = record.Item with { FullPath = Move(record.Item.FullPath)!, ParentPath = Move(record.Item.ParentPath) } });
                Publish();
            }
    }
}
