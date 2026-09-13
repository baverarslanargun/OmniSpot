namespace SmartFileLauncher.Core.Search;

internal sealed partial class LiveCatalog
{
    internal CatalogReader CreateReader()
    {
        _gate.EnterWriteLock();
        try
        {
            ObjectDisposedException.ThrowIf(_disposeRequested, this);
            if (!_sealed) throw new InvalidOperationException("Compact köprüsü tamamlanmış Live katalog gerektirir.");
            var size = checked((int)UsedBytes);
            var reader = new SealedReader(this, _stamp, size);
            _readerLeases++; return reader;
        }
        finally { _gate.ExitWriteLock(); }
    }
    internal CompactSearchState CreateSearchState() => CompactSearchState.FromCatalog(CreateReader());

    private sealed class SealedReader : CatalogReader, IDirectCatalogMatches
    {
        private readonly LiveCatalog _owner;
        private readonly LiveReadStamp _stamp;
        private readonly int _size;
        internal SealedReader(LiveCatalog owner, LiveReadStamp stamp, int size) { _owner = owner; _stamp = stamp; _size = size; }
        ~SealedReader() { _owner.ReleaseReader(); }
        private T Keep<T>(T result) { GC.KeepAlive(this); return result; }
        private int Node(int id) => id >= 0 && id < _stamp.Nodes ? id + 1 : throw new ArgumentOutOfRangeException(nameof(id));
        internal override bool RequiresOwnCheckpoint => true;
        internal override int ItemCount => _stamp.Items;
        internal override int IdCapacity => _stamp.Nodes;
        internal override IEnumerable<int> ActiveIds
        {
            get { try { for (var id = 1; id <= _stamp.Nodes; id++) if (_owner.Arrived(id, _stamp)) yield return id - 1; } finally { GC.KeepAlive(this); } }
        }
        internal override int TokenCount => _stamp.ActiveTerms < 0 ? _stamp.Terms : _stamp.ActiveTerms;
        internal override int MissingParentCount => 0;
        internal override int PayloadBytes => _size;
        internal override bool UsesVarint => true;
        internal override long Generation => _owner._indexedUtc;
        internal override SearchItem GetItem(int id, Dictionary<int, string>? pathCache = null) =>
            Keep(_owner.ReadItem(Node(id), _stamp, pathCache, out _) ?? throw new InvalidDataException("Live kayıt eksik."));
        internal override SearchItem GetTransientItem(int id, Dictionary<int, string> sharedPrefixPaths) => GetItem(id, sharedPrefixPaths);
        internal override bool Matches(int id, QueryCatalogFilter filter)
        {
            var meta = _owner.ReadMetadata(Node(id));
            return Keep(filter.Matches((meta.Flags & 1) != 0, meta.Size, meta.Created, meta.Modified));
        }
        internal override int Find(string path, Dictionary<int, string>? pathCache = null) => Keep(_owner.FindPath(path) - 1);
        internal override IEnumerable<int> Roots() { if (Keep(_owner.Arrived(1, _stamp))) yield return 0; }
        internal override IEnumerable<string> Tokens
        {
            get
            {
                try
                {
                    for (var term = 1; term <= _stamp.Terms; term++)
                    {
                        if (_owner.TermInactive(term)) continue;
                        var offset = _owner._terms.Int32(_owner.Term(term));
                        yield return _owner._names.Text(ref offset, _stamp.NamesEnd)!;
                    }
                }
                finally { GC.KeepAlive(this); }
            }
        }
        internal override bool ContainsToken(string token) => Keep(_owner.Postings(_owner.FindTerm(token), _stamp).Any());
        public string CanonicalToken(string token)
        {
            var term = _owner.FindTerm(token);
            if (term == 0) return Keep(token);
            var offset = _owner._terms.Int32(_owner.Term(term));
            return Keep(_owner._names.Text(ref offset, _stamp.NamesEnd)!);
        }
        internal override IEnumerable<int> Posting(string token)
        {
            try { foreach (var node in _owner.Postings(_owner.FindTerm(token), _stamp)) yield return node - 1; }
            finally { GC.KeepAlive(this); }
        }
        public IEnumerable<int> MatchingItems(string token, int? distance, CancellationToken ct)
        {
            try
            {
                foreach (var term in _owner.MatchingTermIds(_stamp, token, distance, ct))
                    foreach (var node in _owner.Postings(term, _stamp)) { ct.ThrowIfCancellationRequested(); yield return node - 1; }
            }
            finally { GC.KeepAlive(this); }
        }
        internal override IEnumerable<string> MatchingTokens(string token, int? distance, CancellationToken ct)
        {
            try
            {
                foreach (var term in _owner.MatchingTermIds(_stamp, token, distance, ct))
                {
                    var offset = _owner._terms.Int32(_owner.Term(term));
                    yield return _owner._names.Text(ref offset, _stamp.NamesEnd)!;
                }
            }
            finally { GC.KeepAlive(this); }
        }
        internal override IEnumerable<int> Children(string path, Dictionary<int, string>? pathCache = null)
        {
            try
            {
                var parent = _owner.FindPath(path);
                if (parent != 0) foreach (var node in _owner.ChildIds(parent)) if (_owner.Arrived(node, _stamp)) yield return node - 1;
            }
            finally { GC.KeepAlive(this); }
        }
        internal override string[] ItemTokens(int id) => new BasicTokenizer().Tokenize(GetItem(id).Name).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        internal override void WriteNew(string path) => throw new InvalidOperationException("Live katalog sayfaları kendi kalıcı checkpoint yöntemiyle kaydedilmeli.");
    }
}
