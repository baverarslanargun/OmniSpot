using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Index;

public readonly record struct IndexEntry(
    ulong FileReference,
    ulong ParentReference,
    string Name,
    FileAttributes Attributes,
    long SizeBytes,
    long LastWriteTimeUtc,
    long CreatedTimeUtc = 0)
{
    public bool IsDirectory => (Attributes & FileAttributes.Directory) != 0;

    public static IndexEntry FromMft(NtfsMftEntry entry) => new(
        entry.FileReference,
        entry.ParentFileReference,
        entry.Name,
        entry.IsDirectory ? entry.Attributes | FileAttributes.Directory : entry.Attributes & ~FileAttributes.Directory,
        entry.IsDirectory ? 0 : entry.SizeBytes,
        entry.LastWriteTimeUtc,
        entry.CreatedTimeUtc);
}

public readonly record struct IndexCheckpoint(ulong VolumeSerial, ulong JournalId, long NextUsn);

public sealed partial class FileIndex : IDisposable
{
    internal const uint NoRow = uint.MaxValue;
    private const uint DeadBit = 0x8000_0000;
    private const ulong RecordMask = 0x0000_FFFF_FFFF_FFFF;
    private const int FirstUserRecord = 16;
    private readonly ReaderWriterLockSlim _lock = new(LockRecursionPolicy.NoRecursion);
    private IndexNames _names = new();
    private ulong[] _frn = new ulong[1024];
    private uint[] _parent = new uint[1024];
    private int[] _name = new int[1024];
    private uint[] _attributes = new uint[1024];
    private long[] _size = new long[1024];
    private long[] _modified = new long[1024];
    private uint[] _created = new uint[1024];
    private int _count;
    private int _dead;
    private int[] _rowByRecord = new int[1024];
    private readonly Dictionary<uint, uint> _nextLink = [];
    private uint _root = NoRow;
    private bool _compacted;
    private int _generation;
    private byte[] _rowScratch = [];
    private ulong[] _bitScratch = [];

    private FileIndex(string rootPath, ulong rootReference)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(rootPath);
        RootPath = rootPath.EndsWith(Path.DirectorySeparatorChar) ? rootPath : rootPath + Path.DirectorySeparatorChar;
        RootReference = rootReference;
    }

    public string RootPath { get; }

    public ulong RootReference { get; }

    public int Count
    {
        get
        {
            _lock.EnterReadLock();
            try { return _count - _dead - 1; }
            finally { _lock.ExitReadLock(); }
        }
    }

    public static FileIndex Build(string rootPath, ulong rootReference, IEnumerable<IndexEntry> entries,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(entries);
        var index = new FileIndex(rootPath, rootReference);
        var parents = new ulong[1024];
        var seen = 0;
        foreach (var entry in entries)
        {
            if ((++seen & 4095) == 0) cancellationToken.ThrowIfCancellationRequested();
            if (index.IsHiddenRecord(entry.FileReference)) continue;
            var row = index.AddRow(entry);
            IndexArrays.Grow(ref parents, (int)row + 1);
            parents[row] = entry.ParentReference;
        }
        if (index._root == NoRow) throw new InvalidDataException("Kök klasör kaydı bulunamadı.");
        for (var row = 0u; row < index._count; row++)
        {
            if (row == index._root) { index._parent[row] = NoRow; continue; }
            var parent = index.FindDirectory(parents[row]);
            index._parent[row] = parent;
        }
        index.DropDisconnected();
        index.RebuildOrders(cancellationToken);
        index.Trim();
        return index;
    }

    private void Trim()
    {
        var size = _count + Math.Max(1024, _count / 64);
        Array.Resize(ref _frn, size);
        Array.Resize(ref _parent, size);
        Array.Resize(ref _name, size);
        Array.Resize(ref _attributes, size);
        Array.Resize(ref _size, size);
        Array.Resize(ref _modified, size);
        Array.Resize(ref _created, size);
        var records = _rowByRecord.Length;
        while (records > 1 && _rowByRecord[records - 1] == 0) records--;
        Array.Resize(ref _rowByRecord, records + Math.Max(1024, records / 64));
        _names.Trim();
    }

    private bool IsHiddenRecord(ulong reference) =>
        reference != RootReference && (reference & RecordMask) < FirstUserRecord;

    private uint AddRow(IndexEntry entry)
    {
        var row = checked((uint)_count);
        var size = _count + 1;
        IndexArrays.Grow(ref _frn, size);
        IndexArrays.Grow(ref _parent, size);
        IndexArrays.Grow(ref _name, size);
        IndexArrays.Grow(ref _attributes, size);
        IndexArrays.Grow(ref _size, size);
        IndexArrays.Grow(ref _modified, size);
        IndexArrays.Grow(ref _created, size);
        _frn[row] = entry.FileReference;
        _parent[row] = NoRow;
        _name[row] = _names.Intern(entry.Name);
        _attributes[row] = (uint)entry.Attributes & ~DeadBit;
        _size[row] = entry.IsDirectory ? 0 : entry.SizeBytes;
        _modified[row] = entry.LastWriteTimeUtc;
        _created[row] = CreatedSeconds(entry.CreatedTimeUtc);
        _count = size;
        LinkRecord(row);
        if (entry.FileReference == RootReference) _root = row;
        return row;
    }

    private int RecordOf(ulong reference) => checked((int)(reference & RecordMask));

    private void LinkRecord(uint row)
    {
        var record = RecordOf(_frn[row]);
        IndexArrays.Grow(ref _rowByRecord, record + 1);
        var first = _rowByRecord[record] - 1;
        if (first < 0)
        {
            _rowByRecord[record] = (int)row + 1;
            return;
        }
        var last = (uint)first;
        while (_nextLink.TryGetValue(last, out var next)) last = next;
        _nextLink[last] = row;
    }

    private void UnlinkRecord(uint row)
    {
        var record = RecordOf(_frn[row]);
        if (record >= _rowByRecord.Length) return;
        var first = _rowByRecord[record] - 1;
        if (first < 0) return;
        var hasNext = _nextLink.Remove(row, out var following);
        if ((uint)first == row)
        {
            _rowByRecord[record] = hasNext ? (int)following + 1 : 0;
            return;
        }
        for (var at = (uint)first; _nextLink.TryGetValue(at, out var candidate); at = candidate)
        {
            if (candidate != row) continue;
            if (hasNext) _nextLink[at] = following; else _nextLink.Remove(at);
            return;
        }
    }

    private IEnumerable<uint> RowsOf(ulong reference)
    {
        var record = RecordOf(reference);
        if (record >= _rowByRecord.Length) yield break;
        var first = _rowByRecord[record] - 1;
        if (first < 0) yield break;
        for (var row = (uint)first; ; )
        {
            if (_frn[row] == reference && IsAlive(row)) yield return row;
            if (!_nextLink.TryGetValue(row, out var next)) yield break;
            row = next;
        }
    }

    private uint FindDirectory(ulong reference)
    {
        foreach (var row in RowsOf(reference))
            if (IsDirectory(row)) return row;
        return NoRow;
    }

    private void DropDisconnected()
    {
        var state = new byte[_count];
        var chain = new List<uint>();
        state[_root] = 2;
        for (var row = 0u; row < _count; row++)
        {
            if (!IsAlive(row) || state[row] != 0) continue;
            chain.Clear();
            var at = row;
            byte result;
            while (true)
            {
                if (at == NoRow || !IsAlive(at) || !IsDirectory(at) && at != row) { result = 3; break; }
                if (state[at] == 2 || state[at] == 3) { result = state[at]; break; }
                if (state[at] == 1) { result = 3; break; }
                state[at] = 1;
                chain.Add(at);
                at = _parent[at];
            }
            foreach (var member in chain) state[member] = result;
        }
        for (var row = 0u; row < _count; row++)
            if (IsAlive(row) && state[row] == 3) Kill(row);
    }

    internal bool IsAlive(uint row) => (_attributes[row] & DeadBit) == 0;

    internal bool IsDirectory(uint row) => (_attributes[row] & (uint)FileAttributes.Directory) != 0;

    private void Kill(uint row)
    {
        if (!IsAlive(row)) return;
        UnlinkRecord(row);
        _attributes[row] |= DeadBit;
        _dead++;
    }

    internal string NameOf(uint row) => row == _root ? RootPath : _names.Text(_name[row]);

    internal string PathOf(uint row)
    {
        if (row == _root) return RootPath;
        var parts = new List<int>(16);
        for (var at = row; at != _root && at != NoRow; at = _parent[at]) parts.Add(_name[at]);
        var builder = new System.Text.StringBuilder(RootPath, RootPath.Length + parts.Count * 16);
        for (var i = parts.Count - 1; i >= 0; i--)
        {
            builder.Append(_names.Text(parts[i]));
            if (i > 0) builder.Append(Path.DirectorySeparatorChar);
        }
        return builder.ToString();
    }

    private static uint CreatedSeconds(long ticks) =>
        ticks <= DateTime.UnixEpoch.Ticks ? 0u
            : (uint)Math.Min(uint.MaxValue, (ticks - DateTime.UnixEpoch.Ticks) / TimeSpan.TicksPerSecond);

    private DateTime? CreatedOf(uint row) =>
        _created[row] == 0 ? null : DateTime.UnixEpoch.AddSeconds(_created[row]).ToLocalTime();

    private DateTime ModifiedOf(uint row) => new DateTime(_modified[row], DateTimeKind.Utc).ToLocalTime();

    private SearchResult ToResult(uint row)
    {
        var directory = IsDirectory(row);
        return new SearchResult
        {
            Name = NameOf(row),
            FullPath = PathOf(row),
            IsDirectory = directory,
            SizeBytes = directory ? null : _size[row],
            LastWriteTime = directory ? null : new DateTime(_modified[row], DateTimeKind.Utc).ToLocalTime()
        };
    }

    public void Dispose() => _lock.Dispose();
}
