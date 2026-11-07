using System.IO.MemoryMappedFiles;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SmartFileLauncher.Core.Search.Paged;

internal unsafe sealed class Segment : IDisposable
{
    internal const int PageSize = 4096, Capacity = 64 * 1024 * 1024;
    internal readonly string Path;
    internal readonly bool Writable;
    internal byte* Pointer;
    internal int Length, Used;
    int _refs = 1;
    MemoryMappedFile? _map; MemoryMappedViewAccessor? _view; FileStream? _file;
    internal Segment(string path, bool writable, int length = 0)
    {
        Path = path; Writable = writable;
        _file = new FileStream(path, writable ? FileMode.CreateNew : FileMode.Open, writable ? FileAccess.ReadWrite : FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        if (writable) _file.SetLength(length == 0 ? Capacity : length);
        Length = checked((int)_file.Length); if (Length <= 0 || Length % PageSize != 0) throw new InvalidDataException("Segment length");
        _map = MemoryMappedFile.CreateFromFile(_file, null, 0, writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read, HandleInheritability.None, true);
        _view = _map.CreateViewAccessor(0, Length, writable ? MemoryMappedFileAccess.ReadWrite : MemoryMappedFileAccess.Read);
        byte* p = null; _view.SafeMemoryMappedViewHandle.AcquirePointer(ref p); Pointer = p + _view.PointerOffset;
    }
    internal Segment Retain() { Interlocked.Increment(ref _refs); return this; }
    internal int Allocate() { if (!Writable || Used + PageSize > Length) throw new InvalidOperationException(); var result = Used; Used += PageSize; return result; }
    void Unmap() { if (_view is not null) { _view.SafeMemoryMappedViewHandle.ReleasePointer(); _view.Dispose(); _view = null; } _map?.Dispose(); _map = null; Pointer = null; }
    internal void Seal()
    {
        if (!Writable) throw new InvalidOperationException(); _view!.Flush(); _file!.Flush(true); Unmap(); _file.SetLength(Used); _file.Flush(true); _file.Dispose(); _file = null;
    }
    public void Dispose() { if (Interlocked.Decrement(ref _refs) != 0) return; Unmap(); _file?.Dispose(); _file = null; }
}
internal unsafe sealed class Page
{
    internal readonly Segment Segment; internal readonly int Offset; internal readonly byte[]? Hash;
    int _verified;
    internal Page(Segment segment, int offset, byte[]? hash = null, bool verified = false) { Segment = segment; Offset = offset; Hash = hash; if (hash is null || verified) _verified = 1; }
    internal ReadOnlySpan<byte> Read()
    {
        var span = new ReadOnlySpan<byte>(Segment.Pointer + Offset, Segment.PageSize);
        if (_verified == 0) { Span<byte> actual = stackalloc byte[32]; SHA256.HashData(span, actual); if (!actual.SequenceEqual(Hash)) throw new InvalidDataException("Page checksum"); Volatile.Write(ref _verified, 1); }
        return span;
    }
    internal Span<byte> Write() => new(Segment.Pointer + Offset, Segment.PageSize);
}
internal sealed class Area
{
    internal readonly List<Page> Pages = []; internal long Length; internal readonly int RowSize;
    internal Area(int size) => RowSize = size;
    internal Area Clone() { var area = new Area(RowSize) { Length = Length }; area.Pages.AddRange(Pages); return area; }
}
internal unsafe sealed class Image : IDisposable
{
    internal static readonly int[] Sizes = [20, 24, 1, 1, 4, 4, 24, 1, 1, 4, 16, 8, 4, 16];
    internal const int Files = 0, Dirs = 1, Names = 2, Metadata = 3, FileHash = 4, NameHash = 5, Words = 6, WordText = 7, Postings = 8, WordHash = 9, Children = 10, Aliases = 11, NameOrder = 12, Grams = 13;
    internal readonly Area[] Areas;
    internal readonly string Directory;
    internal long Generation, Cursor;
    internal ulong RootFrn;
    internal int EntryCount, HashSize, NameHashSize, WordHashSize, AliasSize, SortedCount, ReplayDepth, NameUsed, AliasUsed, FileFree, DirFree;
    internal string RootPath = "C:\\", VolumeIdentity = "fixture", Frame = "";
    internal string DeliveryId = "";
    internal bool TermsReady = true;
    internal int TokenBuildCursor;
    internal bool Mutable;
    readonly HashSet<Segment> _segments = [];
    readonly Dictionary<(int, int), Page> _dirty = [];
    readonly List<Segment> _draft = [];
    readonly HashSet<string> _frames = [];
    bool _disposed;
    internal Image(string directory, bool mutable)
    { Directory = System.IO.Path.GetFullPath(directory); Areas = Sizes.Select(x => new Area(x)).ToArray(); Mutable = mutable; }
    internal Image Fork()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var next = new Image(Directory, true) { Generation = Generation, Cursor = Cursor, RootFrn = RootFrn, EntryCount = EntryCount, HashSize = HashSize, NameHashSize = NameHashSize, WordHashSize = WordHashSize, AliasSize = AliasSize, SortedCount = SortedCount, RootPath = RootPath, VolumeIdentity = VolumeIdentity, Frame = Frame, ReplayDepth = ReplayDepth, NameUsed = NameUsed, AliasUsed = AliasUsed, FileFree = FileFree, DirFree = DirFree };
        next.DeliveryId = DeliveryId;
        next.TermsReady = TermsReady;
        next.TokenBuildCursor = TokenBuildCursor;
        for (var i = 0; i < Areas.Length; i++) next.Areas[i] = Areas[i].Clone();
        foreach (var segment in _segments) next._segments.Add(segment.Retain()); next._frames.UnionWith(_frames); return next;
    }
    Page AllocatePage()
    {
        if (_draft.Count == 0 || _draft[^1].Used == _draft[^1].Length)
        { var s = new Segment(System.IO.Path.Combine(Directory, "seg-" + Guid.NewGuid().ToString("N") + ".bin"), true); _draft.Add(s); _segments.Add(s); }
        var segment = _draft[^1]; return new Page(segment, segment.Allocate());
    }
    internal Page WritePage(int area, int index)
    {
        if (!Mutable) throw new InvalidOperationException("Immutable snapshot");
        var pages = Areas[area].Pages;
        if (index < pages.Count && pages[index].Segment.Writable) return pages[index];
        while (pages.Count <= index) { var fresh = AllocatePage(); pages.Add(fresh); _dirty[(area, pages.Count - 1)] = fresh; }
        if (pages[index].Segment.Writable) return pages[index];
        var page = AllocatePage(); pages[index].Read().CopyTo(page.Write()); pages[index] = page; _dirty[(area, index)] = page; return page;
    }
    internal long Count(int area) => Areas[area].Length;
    internal T Read<T>(int area, int index) where T : unmanaged
    {
        var size = Areas[area].RowSize; var per = Segment.PageSize / size;
        if (index < 0 || index >= Areas[area].Length) throw new InvalidDataException("Row bounds");
        return MemoryMarshal.Read<T>(Areas[area].Pages[index / per].Read().Slice(index % per * size, size));
    }
    internal void Write<T>(int area, int index, T value) where T : unmanaged
    {
        var size = Areas[area].RowSize; if (sizeof(T) != size) throw new InvalidOperationException("Row size"); var per = Segment.PageSize / size;
        MemoryMarshal.Write(WritePage(area, index / per).Write().Slice(index % per * size, size), in value); Areas[area].Length = Math.Max(Areas[area].Length, index + 1L);
    }
    internal int Add<T>(int area, T value) where T : unmanaged { var at = checked((int)Count(area)); Write(area, at, value); return at; }
    internal ReadOnlySpan<byte> Bytes(int area, int offset, int length)
    {
        if (offset < 0 || length < 0 || (long)offset + length > Areas[area].Length || offset / Segment.PageSize != (offset + Math.Max(0, length - 1)) / Segment.PageSize) throw new InvalidDataException("Byte bounds");
        if (length == 0) return []; return Areas[area].Pages[offset / Segment.PageSize].Read().Slice(offset % Segment.PageSize, length);
    }
    internal Span<byte> MutableBytes(int area, int offset, int length) => WritePage(area, offset / Segment.PageSize).Write().Slice(offset % Segment.PageSize, length);
    internal int Append(int area, ReadOnlySpan<byte> bytes)
    {
        if (bytes.Length > Segment.PageSize) throw new InvalidDataException("Entry exceeds page");
        var at = checked((int)Areas[area].Length); if (at % Segment.PageSize + bytes.Length > Segment.PageSize) at = (at / Segment.PageSize + 1) * Segment.PageSize;
        bytes.CopyTo(WritePage(area, at / Segment.PageSize).Write().Slice(at % Segment.PageSize, bytes.Length)); Areas[area].Length = at + bytes.Length; return at;
    }
    internal void Reset(int area) { if (!Mutable) throw new InvalidOperationException(); Areas[area].Pages.Clear(); Areas[area].Length = 0; foreach (var key in _dirty.Keys.Where(x => x.Item1 == area).ToArray()) _dirty.Remove(key); }
    internal void ZeroRows(int area, int count)
    { Reset(area); var per = Segment.PageSize / Areas[area].RowSize; for (int i = 0; i < (count + per - 1) / per; i++) WritePage(area, i); Areas[area].Length = count; }
    internal long MappedBytes => _segments.Where(x => !x.Writable).Sum(x => (long)x.Length);
    internal long LivePageBytes => Areas.Sum(x => (long)x.Pages.Count * Segment.PageSize);
    internal object Ledger() => Areas.Select((x, i) => new { Area = i, RowSize = x.RowSize, Length = x.Length, PageBytes = (long)x.Pages.Count * Segment.PageSize }).ToArray();
    internal void Commit(long cursor, bool checkpoint = false, string fault = "")
    {
        if (!Mutable) throw new InvalidOperationException();
        var replacements = new Dictionary<Segment, Segment>();
        foreach (var segment in _draft) { segment.Seal(); var mapped = new Segment(segment.Path, false); replacements.Add(segment, mapped); _segments.Add(mapped); }
        foreach (var pair in _dirty.ToArray())
        { var old = pair.Value; var next = new Page(replacements[old.Segment], old.Offset, SHA256.HashData(new ReadOnlySpan<byte>(replacements[old.Segment].Pointer + old.Offset, Segment.PageSize)), verified: true); Areas[pair.Key.Item1].Pages[pair.Key.Item2] = next; _dirty[pair.Key] = next; }
        foreach (var old in _draft) { _segments.Remove(old); old.Dispose(); }
        _draft.Clear();
        if (fault == "pages") throw new IOException("Injected before frame");
        var full = checkpoint || Frame.Length == 0 || ReplayDepth >= 63; Generation++; Cursor = cursor;
        var name = "frame-" + Generation.ToString("D12") + "-" + Guid.NewGuid().ToString("N") + ".bin";
        using var body = new MemoryStream(); using (var w = new BinaryWriter(body, Encoding.UTF8, true))
        {
            w.Write(0x32474d49); w.Write(7); w.Write(Generation); w.Write(Cursor); w.Write(RootFrn); w.Write(EntryCount); w.Write(HashSize); w.Write(NameHashSize); w.Write(WordHashSize); w.Write(AliasSize); w.Write(SortedCount); w.Write(NameUsed); w.Write(AliasUsed); w.Write(FileFree); w.Write(DirFree); w.Write(Convert.ToBase64String(MemoryMarshal.AsBytes(RootPath.AsSpan()))); w.Write(VolumeIdentity); w.Write(DeliveryId); w.Write(TermsReady); w.Write(TokenBuildCursor); w.Write(full ? "" : Frame); w.Write(Areas.Length);
            for (var i = 0; i < Areas.Length; i++) { w.Write(Areas[i].RowSize); w.Write(Areas[i].Length); w.Write(Areas[i].Pages.Count); }
            var refs = full ? Areas.SelectMany((a, i) => a.Pages.Select((p, j) => (Area: i, Index: j, Page: p))).ToArray() : _dirty.Select(x => (Area: x.Key.Item1, Index: x.Key.Item2, Page: x.Value)).ToArray();
            w.Write(refs.Length); foreach (var entry in refs) { w.Write(entry.Area); w.Write(entry.Index); w.Write(System.IO.Path.GetFileName(entry.Page.Segment.Path)); w.Write(entry.Page.Offset); w.Write(entry.Page.Hash ?? throw new InvalidDataException()); }
        }
        WriteChecked(System.IO.Path.Combine(Directory, name), body.ToArray());
        if (fault == "frame") throw new IOException("Injected before head");
        using (var gate = Gate(Directory)) { var head = System.IO.Path.Combine(Directory, "head.bin"); var previous = File.Exists(head) ? Encoding.UTF8.GetString(ReadChecked(head, 4096)) : ""; if (previous != Frame) throw new IOException("Stale writer generation"); WriteChecked(System.IO.Path.Combine(Directory, "head.tmp"), Encoding.UTF8.GetBytes(name)); File.Move(System.IO.Path.Combine(Directory, "head.tmp"), head, true); }
        Frame = name; ReplayDepth = full ? 0 : ReplayDepth + 1; if (full) _frames.Clear(); _frames.Add(name); Mutable = false; _dirty.Clear(); PruneSegments();
    }
    void PruneSegments()
    { var used = Areas.SelectMany(x => x.Pages).Select(x => x.Segment).ToHashSet(); foreach (var segment in _segments.Where(x => !used.Contains(x)).ToArray()) { _segments.Remove(segment); segment.Dispose(); } }
    internal static FileStream Gate(string directory, bool readOnly = false)
    {
        var path = System.IO.Path.Combine(directory, ".gate"); var start = Environment.TickCount64;
        while (true) { try { return new FileStream(path, readOnly ? FileMode.Open : FileMode.OpenOrCreate, readOnly ? FileAccess.Read : FileAccess.ReadWrite, FileShare.None); } catch (IOException) when (Environment.TickCount64 - start < 10000) { Thread.Sleep(2); } }
    }
    internal static void WriteChecked(string path, byte[] body)
    { using var f = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.Read); f.Write(body); f.Write(SHA256.HashData(body)); f.Flush(true); }
    internal static byte[] ReadChecked(string path, int maximum = 32 * 1024 * 1024)
    { using var f = File.OpenRead(path); if (f.Length < 32 || f.Length > maximum) throw new InvalidDataException("Frame size"); var body = new byte[f.Length - 32]; f.ReadExactly(body); Span<byte> hash = stackalloc byte[32]; f.ReadExactly(hash); if (!SHA256.HashData(body).AsSpan().SequenceEqual(hash)) throw new InvalidDataException("Frame checksum"); return body; }
    internal static Image Open(string directory,string? selectedFrame=null, Image? previous = null)
    {
        using var gate = Gate(directory, readOnly: true); var current = selectedFrame ?? Encoding.UTF8.GetString(ReadChecked(System.IO.Path.Combine(directory, "head.bin"), 4096));
        var image = new Image(directory, false); var chain = new List<byte[]>(); var seen = new HashSet<string>(); var frame = current;
        if (previous is not null && (previous.Mutable || previous.Directory != image.Directory)) throw new InvalidOperationException("Reader baseline");
        while (frame.Length != 0 && frame != previous?.Frame)
        { ValidateLeaf(frame); if (!seen.Add(frame) || seen.Count > 64) throw new InvalidDataException("Generation chain"); var bytes = ReadChecked(System.IO.Path.Combine(directory, frame)); chain.Add(bytes); using var r = new BinaryReader(new MemoryStream(bytes)); ReadHeader(r, image); frame = r.ReadString(); }
        var reused = previous is not null && frame == previous.Frame;
        if (reused) { image.Dispose(); image = previous!.Fork(); image.Mutable = false; }
        var segments = image._segments.ToDictionary(segment => System.IO.Path.GetFileName(segment.Path));
        try
        {
            for (var k = chain.Count - 1; k >= 0; k--)
            {
                using var r = new BinaryReader(new MemoryStream(chain[k])); ReadHeader(r, image); r.ReadString();
                if (r.ReadInt32() != Sizes.Length) throw new InvalidDataException("Area count");
                for (int i = 0; i < Sizes.Length; i++) { if (r.ReadInt32() != Sizes[i]) throw new InvalidDataException("Schema"); var length = r.ReadInt64(); var pages = r.ReadInt32(); if (length < 0 || pages < 0 || pages > 2_000_000) throw new InvalidDataException("Area bounds"); image.Areas[i].Length = length; while (image.Areas[i].Pages.Count > pages) image.Areas[i].Pages.RemoveAt(image.Areas[i].Pages.Count - 1); while (image.Areas[i].Pages.Count < pages) image.Areas[i].Pages.Add(null!); }
                var changes = r.ReadInt32(); if (changes < 0 || changes > 2_000_000) throw new InvalidDataException();
                for (int i = 0; i < changes; i++)
                {
                    var area = r.ReadInt32(); var page = r.ReadInt32(); var file = r.ReadString(); ValidateLeaf(file); var offset = r.ReadInt32(); var hash = r.ReadBytes(32); if (area < 0 || area >= Sizes.Length || page < 0 || page >= image.Areas[area].Pages.Count || hash.Length != 32) throw new InvalidDataException("Page ref");
                    if (!segments.TryGetValue(file, out var segment)) { segment = new Segment(System.IO.Path.Combine(directory, file), false); segments.Add(file, segment); image._segments.Add(segment); }
                    if (offset < 0 || offset % Segment.PageSize != 0 || offset + Segment.PageSize > segment.Length) throw new InvalidDataException("Page offset"); image.Areas[area].Pages[page] = new Page(segment, offset, hash);
                }
                if (r.BaseStream.Position != r.BaseStream.Length) throw new InvalidDataException("Frame trailing bytes");
            }
            foreach (var area in image.Areas) if (area.Pages.Any(x => x is null)) throw new InvalidDataException("Missing page");
            image.Frame = current; image._frames.UnionWith(seen); image.ReplayDepth = reused ? previous!.ReplayDepth + chain.Count : chain.Count - 1; image.PruneSegments(); return image;
        }
        catch { image.Dispose(); throw; }
    }
    static void ReadHeader(BinaryReader r, Image image)
    {
        if (r.ReadInt32() != 0x32474d49) throw new InvalidDataException("Schema");
        var version = r.ReadInt32();
        if (version is not (4 or 5 or 6 or 7)) throw new InvalidDataException("Schema");
        image.Generation = r.ReadInt64(); image.Cursor = r.ReadInt64(); image.RootFrn = r.ReadUInt64(); image.EntryCount = r.ReadInt32(); image.HashSize = r.ReadInt32(); image.NameHashSize = r.ReadInt32(); image.WordHashSize = r.ReadInt32(); image.AliasSize = r.ReadInt32(); image.SortedCount = r.ReadInt32(); image.NameUsed = r.ReadInt32(); image.AliasUsed = r.ReadInt32(); image.FileFree = r.ReadInt32(); image.DirFree = r.ReadInt32();
        image.RootPath = r.ReadString();
        if (version >= 5)
        {
            var bytes = Convert.FromBase64String(image.RootPath);
            if (bytes.Length == 0 || bytes.Length % 2 != 0) throw new InvalidDataException("Root encoding");
            image.RootPath = new string(MemoryMarshal.Cast<byte, char>(bytes));
        }
        image.VolumeIdentity = r.ReadString();
        image.DeliveryId = version >= 6 ? r.ReadString() : "";
        image.TermsReady = version < 7 || r.ReadBoolean();
        image.TokenBuildCursor = version >= 7 ? r.ReadInt32() : 0;
        if (image.TokenBuildCursor < 0) throw new InvalidDataException("Token cursor");
    }
    static void ValidateLeaf(string name) { if (name != System.IO.Path.GetFileName(name) || name.Contains(':') || name is "." or "..") throw new InvalidDataException("File reference"); }
    internal void ValidateAll() { foreach (var a in Areas) foreach (var p in a.Pages) p.Read(); }
    internal int Reclaim()
    {
        if (ReplayDepth != 0) throw new InvalidOperationException("Reclamation requires a full checkpoint");
        using var gate = Gate(Directory); if (Encoding.UTF8.GetString(ReadChecked(System.IO.Path.Combine(Directory, "head.bin"), 4096)) != Frame) throw new IOException("Stale cleanup generation");
        var keep = _frames.Concat(_segments.Select(s => System.IO.Path.GetFileName(s.Path))).ToHashSet(StringComparer.OrdinalIgnoreCase); int removed = 0;
        foreach (var file in System.IO.Directory.EnumerateFiles(Directory)) { var leaf = System.IO.Path.GetFileName(file); if (!(leaf.StartsWith("seg-") || leaf.StartsWith("frame-")) || !leaf.EndsWith(".bin") || keep.Contains(leaf)) continue; try { File.Delete(file); removed++; } catch (IOException) { } }
        return removed;
    }
    public void Dispose() { if (_disposed) return; _disposed = true; foreach (var s in _segments) s.Dispose(); _segments.Clear(); }
}
