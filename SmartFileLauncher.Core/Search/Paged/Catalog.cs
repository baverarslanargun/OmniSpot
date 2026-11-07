using System.Buffers.Binary;
using System.Runtime.InteropServices;
using System.Text;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search;

namespace SmartFileLauncher.Core.Search.Paged;

[StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct FileRow { internal ulong Frn; internal int Parent, Name, Meta; }
[StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct DirectoryRow { internal FileRow File; internal int Children; }
[StructLayout(LayoutKind.Sequential, Pack = 1)] internal struct Pair { internal uint Key, Value; }
internal readonly record struct Metadata(uint Attributes, long Size, long Created, long Modified, int OpenCount, int Length = 0);
internal sealed record Change(ulong Frn, ulong Parent, string Name, NtfsMftEntry? NewEntry, int OpenCount = 0);
internal sealed partial class Catalog : IDisposable
{
    internal readonly Image Data;
    int _references = 1;
    internal long PathRevision { get; private set; }
    bool? _hasHistory;
    readonly HashSet<uint> _reused = [];
    bool HasHistory() { if (_hasHistory.HasValue) return _hasHistory.Value; for (int i = 0; i < Data.AliasSize; i++) { var pair = Data.Read<Pair>(Image.Aliases, i); if ((pair.Key & 0x80000000) != 0 && pair.Value != 0) return (_hasHistory = true).Value; } return (_hasHistory = false).Value; }
    internal const uint RootId = 3;
    const int Dirty = 1 << 30, NameMask = Dirty - 1;
    static readonly UTF8Encoding StrictUtf8 = new(false, true);
    internal Catalog(Image data) => Data = data;
    internal static Catalog Create(string path, ulong root, int expected, string volume = "fixture", string rootPath = "C:\\")
    {
        if (System.IO.Directory.Exists(path)) throw new IOException("New catalog path required"); System.IO.Directory.CreateDirectory(path);
        var data = new Image(path, true) { RootFrn = root, RootPath = rootPath, VolumeIdentity = volume };
        data.Add(Image.Files, default(FileRow)); data.Add(Image.Dirs, default(DirectoryRow)); data.Append(Image.Names, new byte[1]); data.Append(Image.Metadata, new byte[1]); data.Add(Image.Words, default(Word)); data.Add(Image.Children, default(Posting)); data.Append(Image.Postings, new byte[32]);
        data.HashSize = Capacity(expected); data.NameHashSize = Capacity(Math.Max(1024, expected / 2)); data.WordHashSize = Capacity(Math.Max(1024, expected / 3)); data.AliasSize = Capacity(Math.Max(1024, expected / 32));
        data.ZeroRows(Image.FileHash, data.HashSize); data.ZeroRows(Image.NameHash, data.NameHashSize); data.ZeroRows(Image.WordHash, data.WordHashSize); data.ZeroRows(Image.Aliases, data.AliasSize); data.ZeroRows(Image.Grams, 16385);
        var catalog = new Catalog(data); if (catalog.DirectoryId(root) != RootId) throw new InvalidDataException(); return catalog;
    }
    static int Capacity(int expected) { var n = 1024; while ((long)n * 7 < Math.Max(1, expected) * 10L) n = checked(n * 2); return n; }
    internal bool IsDirectory(uint id) => (id & 1) != 0;
    internal FileRow Row(uint id) => IsDirectory(id) ? Data.Read<DirectoryRow>(Image.Dirs, (int)(id >> 1)).File : Data.Read<FileRow>(Image.Files, (int)(id >> 1));
    internal bool Exists(uint id) => id >= 2 && (id >> 1) < Data.Count(IsDirectory(id) ? Image.Dirs : Image.Files);
    internal bool Alive(uint id) => Exists(id) && Row(id).Name > 0;
    internal int MaxId => checked((int)Math.Max(Data.Count(Image.Files) * 2, Data.Count(Image.Dirs) * 2 + 1));
    void Write(uint id, FileRow row)
    { if (IsDirectory(id)) { var at = (int)(id >> 1); var d = Data.Read<DirectoryRow>(Image.Dirs, at); d.File = row; Data.Write(Image.Dirs, at, d); } else Data.Write(Image.Files, (int)(id >> 1), row); }
    static int FrnHash(ulong key, int mask) { key ^= key >> 33; key *= 0xff51afd7ed558ccd; key ^= key >> 33; return (int)key & mask; }
    internal uint Find(ulong frn)
    { var slot = FrnHash(frn, Data.HashSize - 1); for (int n = 0; n < Data.HashSize; n++, slot = (slot + 1) & (Data.HashSize - 1)) { var id = Data.Read<uint>(Image.FileHash, slot); if (id == 0) return 0; if (Row(id).Frn == frn) return id; } throw new InvalidDataException("FRN table full"); }
    void SetFrn(ulong frn, uint id)
    {
        if ((Data.Count(Image.Files) + Data.Count(Image.Dirs)) * 10 >= Data.HashSize * 7L) GrowFrn();
        var slot = FrnHash(frn, Data.HashSize - 1); for (int n = 0; n < Data.HashSize; n++, slot = (slot + 1) & (Data.HashSize - 1)) { var current = Data.Read<uint>(Image.FileHash, slot); if (current == 0 || Row(current).Frn == frn) { if (current == 0 && id == 0) return; Data.Write(Image.FileHash, slot, id); if (id == 0) { var next = (slot + 1) & (Data.HashSize - 1); while (Data.Read<uint>(Image.FileHash, next) != 0) { var move = Data.Read<uint>(Image.FileHash, next); Data.Write(Image.FileHash, next, 0u); SetFrn(Row(move).Frn, move); next = (next + 1) & (Data.HashSize - 1); } } return; } }
        throw new InvalidDataException();
    }
    void GrowFrn()
    { var old = new List<uint>(); for (int i = 0; i < Data.HashSize; i++) { var id = Data.Read<uint>(Image.FileHash, i); if (id != 0) old.Add(id); } Data.HashSize = checked(Data.HashSize * 2); Data.ZeroRows(Image.FileHash, Data.HashSize); foreach (var id in old) SetFrn(Row(id).Frn, id); }
    uint NewId(bool dir)
    {
        var free = dir ? Data.DirFree : Data.FileFree;
        if (free != 0) { var id = (uint)free; _reused.Add(id); var next = Row(id).Parent; if (dir) { Data.DirFree = next; Data.Write(Image.Dirs, (int)(id >> 1), default(DirectoryRow)); } else { Data.FileFree = next; Write(id, default); } return id; }
        return dir ? (uint)(Data.Add(Image.Dirs, default(DirectoryRow)) * 2 + 1) : (uint)(Data.Add(Image.Files, default(FileRow)) * 2);
    }
    uint DirectoryId(ulong frn)
    { var id = Find(frn); if (id != 0) { if (!IsDirectory(id)) throw new InvalidDataException("Parent is a file"); return id; } id = NewId(true); Write(id, new FileRow { Frn = frn }); SetFrn(frn, id); return id; }
    uint AliasNext(uint id)
    { var mask = Data.AliasSize - 1; var slot = FrnHash(id, mask); while (true) { var pair = Data.Read<Pair>(Image.Aliases, slot); if (pair.Key == 0) return 0; if (pair.Key == id) return pair.Value; slot = (slot + 1) & mask; } }
    void SetAlias(uint id, uint next)
    {
        if ((Data.AliasUsed + 1) * 10 >= Data.AliasSize * 7)
        { var old = new List<Pair>(); for (int i = 0; i < Data.AliasSize; i++) { var p = Data.Read<Pair>(Image.Aliases, i); if (p.Key != 0 && p.Value != 0) old.Add(p); } Data.AliasSize *= 2; Data.ZeroRows(Image.Aliases, Data.AliasSize); Data.AliasUsed = 0; foreach (var p in old) SetAlias(p.Key, p.Value); }
        var slot = FrnHash(id, Data.AliasSize - 1); while (true) { var pair = Data.Read<Pair>(Image.Aliases, slot); if (pair.Key == 0) { if (next == 0) return; Data.AliasUsed++; Data.Write(Image.Aliases, slot, new Pair { Key = id, Value = next }); return; } if (pair.Key == id) { pair.Value = next; Data.Write(Image.Aliases, slot, pair); return; } slot = (slot + 1) & (Data.AliasSize - 1); }
    }
    uint FindAlias(ulong frn, uint parent, string name)
    { var head = Find(frn); if (head != 0 && IsDirectory(head)) return head; for (var id = head; id != 0; id = AliasNext(id)) if (Alive(id) && Row(id).Parent == parent && NameEquals(id, name, StringComparison.OrdinalIgnoreCase)) return id; return 0; }
    internal uint Accept(NtfsMftEntry entry, int openCount = 0, bool addTerms = true)
    {
        PathRevision++;
        if (openCount != 0) _hasHistory = true;
        var root = entry.IsDirectory && entry.FileReference == Data.RootFrn;
        var parent = root ? 0 : DirectoryId(entry.ParentFileReference); var id = entry.IsDirectory ? DirectoryId(entry.FileReference) : FindAlias(entry.FileReference, parent, entry.Name);
        var fresh = id == 0; if (fresh) { id = NewId(false); Write(id, new FileRow { Frn = entry.FileReference }); SetAlias(id, Find(entry.FileReference)); SetFrn(entry.FileReference, id); }
        var wasAlive = Alive(id); var row = Row(id); row.Parent = (int)parent;
        var name = entry.Name;
        if (root) { var rootName = System.IO.Path.GetFileName(System.IO.Path.TrimEndingDirectorySeparator(Data.RootPath)); name = rootName.Length == 0 ? Data.RootPath : rootName; }
        var changed = wasAlive && !NameEquals(id, name, StringComparison.Ordinal);
        if (!wasAlive || changed) { row.Name = InternName(name) | (_reused.Remove(id) || !wasAlive && Data.Generation > 0 || changed ? Dirty : 0); Write(id, row); if (addTerms) AddTerms(id, name); }
        row = Row(id); row.Meta = AppendMetadata(new((uint)entry.Attributes, entry.SizeBytes, entry.CreatedTimeUtc, entry.LastWriteTimeUtc, openCount)); Write(id, row); if (openCount != 0 || wasAlive) SetAlias(id | 0x80000000, (uint)openCount);
        if (!wasAlive) { Data.EntryCount++; if (parent != 0) AddChild(parent, id); }
        return id;
    }
    static uint ByteHash(ReadOnlySpan<byte> value) { uint hash = 2166136261; foreach (var b in value) hash = unchecked((hash ^ b) * 16777619); return hash; }
    internal ReadOnlySpan<byte> NameBytes(uint id, out bool utf16) => NameAt(Row(id).Name & NameMask, out utf16);
    ReadOnlySpan<byte> NameAt(int offset, out bool utf16)
    { var length = BinaryPrimitives.ReadUInt16LittleEndian(Data.Bytes(Image.Names, offset, 2)); utf16 = (length & 0x8000) != 0; return Data.Bytes(Image.Names, offset + 2, length & 0x7fff); }
    internal string Name(uint id) { var bytes = NameBytes(id, out var utf16); return utf16 ? new string(MemoryMarshal.Cast<byte, char>(bytes)) : Encoding.UTF8.GetString(bytes); }
    internal bool NameEquals(uint id, string text, StringComparison comparison)
    { var bytes = NameBytes(id, out var utf16); if (utf16) return MemoryMarshal.Cast<byte, char>(bytes).Equals(text.AsSpan(), comparison); if (Ascii(bytes)) { if (bytes.Length != text.Length) return false; for (int i = 0; i < bytes.Length; i++) { var x = (char)bytes[i]; var y = text[i]; if (y > 127) goto Unicode; if (comparison == StringComparison.OrdinalIgnoreCase) { if (x is >= 'a' and <= 'z') x -= (char)32; if (y is >= 'a' and <= 'z') y -= (char)32; } if (x != y) return false; } return true; } Unicode: Span<char> chars = stackalloc char[1024]; var count = Encoding.UTF8.GetChars(bytes, chars); return MemoryExtensions.Equals(chars[..count], text.AsSpan(), comparison); }
    int InternName(string name)
    {
        if (name.Length > 1023) throw new InvalidDataException("Filename too long"); Span<byte> bytes = stackalloc byte[4096]; bool wide = false; int length;
        try { length = StrictUtf8.GetBytes(name, bytes[2..]); } catch (EncoderFallbackException) { wide = true; length = name.Length * 2; MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(bytes[2..]); }
        BinaryPrimitives.WriteUInt16LittleEndian(bytes, (ushort)(length | (wide ? 0x8000 : 0))); var all = bytes[..(length + 2)];
        if ((Data.NameUsed + 1) * 10 >= Data.NameHashSize * 7L) GrowNames();
        var slot = (int)ByteHash(all) & (Data.NameHashSize - 1);
        while (true) { var at = Data.Read<int>(Image.NameHash, slot); if (at == 0) { Span<byte> record = stackalloc byte[4096]; all.CopyTo(record[4..]); record[..4].Clear(); var offset = Data.Append(Image.Names, record[..(all.Length + 4)]) + 4; Data.Write(Image.NameHash, slot, offset); Data.NameUsed++; return offset; } var old = NameAt(at, out _); if (old.Length == length && Data.Bytes(Image.Names, at, length + 2).SequenceEqual(all)) return at; slot = (slot + 1) & (Data.NameHashSize - 1); }
    }
    void GrowNames()
    { var old = new int[Data.NameHashSize]; for (int i = 0; i < old.Length; i++) old[i] = Data.Read<int>(Image.NameHash, i); Data.NameHashSize *= 2; Data.ZeroRows(Image.NameHash, Data.NameHashSize); foreach (var at in old) { if (at == 0) continue; var bytes = NameAt(at, out _); var slot = (int)ByteHash(Data.Bytes(Image.Names, at, bytes.Length + 2)) & (Data.NameHashSize - 1); while (Data.Read<int>(Image.NameHash, slot) != 0) slot = (slot + 1) & (Data.NameHashSize - 1); Data.Write(Image.NameHash, slot, at); } }
    static int Var(ulong value, Span<byte> bytes) { var i = 0; do { bytes[i++] = (byte)((value & 127) | (value >= 128 ? 128UL : 0)); value >>= 7; } while (value != 0); return i; }
    static ulong Unvar(ReadOnlySpan<byte> bytes, ref int at) { ulong value = 0; int shift = 0; byte b; do { if (at >= bytes.Length || shift >= 64) throw new InvalidDataException("Varint"); b = bytes[at++]; value |= (ulong)(b & 127) << shift; shift += 7; } while ((b & 128) != 0); return value; }
    static ulong Zig(long value) => unchecked((ulong)((value << 1) ^ (value >> 63)));
    static long Unzig(ulong value) => unchecked((long)(value >> 1) ^ -((long)value & 1));
    static int Tick(long value, Span<byte> bytes)
    { var best = 9; ulong code = 7; var q = value; for (int e = 0; e < 7; e++) { var z = Zig(q); if (z <= ulong.MaxValue >> 3) { var c = (z << 3) | (uint)e; var n = 1; for (var t = c; t >= 128; t >>= 7) n++; if (n < best) { best = n; code = c; } } if (q % 10 != 0) break; q /= 10; } if (best == 9) { bytes[0] = 7; BinaryPrimitives.WriteInt64LittleEndian(bytes[1..], value); return 9; } return Var(code, bytes); }
    static long Untick(ReadOnlySpan<byte> bytes, ref int at)
    { var v = Unvar(bytes, ref at); var exp = (int)(v & 7); if (exp == 7) { var raw = BinaryPrimitives.ReadInt64LittleEndian(bytes[at..]); at += 8; return raw; } var value = Unzig(v >> 3); while (exp-- > 0) value = checked(value * 10); return value; }
    int AppendMetadata(Metadata value)
    { Span<byte> bytes = stackalloc byte[64]; var n = Var(value.Attributes, bytes); n += Var(Zig(value.Size), bytes[n..]); n += Tick(value.Created, bytes[n..]); n += Tick(checked(value.Modified - value.Created), bytes[n..]); n += Var(Zig(value.OpenCount), bytes[n..]); return Data.Append(Image.Metadata, bytes[..n]); }
    internal Metadata Meta(uint id)
    { var offset = Row(id).Meta; var length = (int)Math.Min(Segment.PageSize - offset % Segment.PageSize, Data.Count(Image.Metadata) - offset); var bytes = Data.Bytes(Image.Metadata, offset, length); var at = 0; var attrs = checked((uint)Unvar(bytes, ref at)); var size = Unzig(Unvar(bytes, ref at)); var created = Untick(bytes, ref at); var modified = checked(created + Untick(bytes, ref at)); var opened = checked((int)Unzig(Unvar(bytes, ref at))); return new(attrs, size, created, modified, opened, at); }
    void AddChild(uint parent, uint child)
    { var d = Data.Read<DirectoryRow>(Image.Dirs, (int)(parent >> 1)); if (d.Children == 0) { d.Children = Data.Add(Image.Children, default(Posting)); Data.Write(Image.Dirs, (int)(parent >> 1), d); } var list = Data.Read<Posting>(Image.Children, d.Children); AddPosting(ref list, child); Data.Write(Image.Children, d.Children, list); }
    internal List<uint> Children(uint parent)
    { var result = new List<uint>(); if (!Alive(parent) || !IsDirectory(parent)) return result; var d = Data.Read<DirectoryRow>(Image.Dirs, (int)(parent >> 1)); if (d.Children == 0) return result; var seen = new HashSet<uint>(); Visit(Data.Read<Posting>(Image.Children, d.Children), id => { if (Alive(id) && Row(id).Parent == parent && seen.Add(id)) result.Add(id); }); return result; }
    internal uint FindChild(uint parent, string name)
    {
        if (!Alive(parent) || !IsDirectory(parent)) return 0;
        var directory = Data.Read<DirectoryRow>(Image.Dirs, (int)(parent >> 1));
        if (directory.Children == 0) return 0;
        var entries = new PostingCursor(Data, Data.Read<Posting>(Image.Children, directory.Children));
        while (entries.MoveNext())
        {
            var id = entries.Current;
            if (Alive(id) && Row(id).Parent == parent && NameEquals(id, name, StringComparison.OrdinalIgnoreCase)) return id;
        }
        return 0;
    }
    internal IEnumerable<uint> ActiveIds() { for (uint i = 1; i < Data.Count(Image.Dirs); i++) if (Alive(i * 2 + 1)) yield return i * 2 + 1; for (uint i = 1; i < Data.Count(Image.Files); i++) if (Alive(i * 2)) yield return i * 2; }
    internal string FullPath(uint id)
    {
        if (id == RootId) return Data.RootPath; var parts = new List<uint>(); for (var current = id; current != RootId; current = (uint)Row(current).Parent) { if (!Alive(current) || parts.Count > 1024) throw new InvalidDataException("Parent cycle/missing"); parts.Add(current); }
        var length = Data.RootPath.TrimEnd('\\').Length; foreach (var part in parts) { var b = NameBytes(part, out var u); length += 1 + (u ? b.Length / 2 : Encoding.UTF8.GetCharCount(b)); }
        return string.Create(length, (Self: this, Parts: parts), static (span, s) => { var pos = 0; var root = s.Self.Data.RootPath.TrimEnd('\\'); root.CopyTo(span); pos = root.Length; for (int i = s.Parts.Count - 1; i >= 0; i--) { span[pos++] = '\\'; var b = s.Self.NameBytes(s.Parts[i], out var u); pos += DecodeName(b, u, span[pos..]); } });
    }
    internal SearchItem Item(uint id)
    { if (!Alive(id)) throw new InvalidDataException(); var row = Row(id); var m = Meta(id); var dir = IsDirectory(id); return new(Name(id), FullPath(id), dir, dir ? null : m.Size, dir ? null : new DateTime(m.Created, DateTimeKind.Utc).ToLocalTime(), dir ? null : new DateTime(m.Modified, DateTimeKind.Utc).ToLocalTime(), m.OpenCount, id == RootId ? "" : FullPath((uint)row.Parent)); }
    internal void Audit()
    {
        if (!Alive(RootId) || Row(RootId).Parent != 0 || Row(RootId).Frn != Data.RootFrn) throw new InvalidDataException("Root"); var state = new byte[Data.Count(Image.Dirs)]; state[1] = 2;
        foreach (var id in ActiveIds())
        { var r = Row(id); if (id != RootId && (!Alive((uint)r.Parent) || !IsDirectory((uint)r.Parent))) throw new InvalidDataException("Missing parent"); if (!IsDirectory(id)) continue; var pending = new List<uint>(); var at = id; while (at != 0 && state[at >> 1] != 2) { if (state[at >> 1] == 1) throw new InvalidDataException("Parent cycle"); state[at >> 1] = 1; pending.Add(at); at = (uint)Row(at).Parent; } foreach (var seen in pending) state[seen >> 1] = 2; }
        if (ActiveIds().Count() != Data.EntryCount || Data.Count(Image.Dirs) - 1 != ActiveIds().Count(IsDirectory) + CountFreeDirs()) throw new InvalidDataException("Unresolved directory");
    }
    int CountFreeDirs() { int n = 0; for (var p = Data.DirFree; p != 0; p = Row((uint)p).Parent) n++; return n; }
    internal int DirectoryCount => checked((int)Data.Count(Image.Dirs) - 1 - CountFreeDirs());
    internal ulong AllocateSyntheticReference()
    {
        var candidate = 0x8000000000000000UL | (uint)MaxId;
        while (Find(candidate) != 0) candidate++;
        return candidate;
    }
    internal Catalog Fork() => new(Data.Fork()) { _hasHistory = _hasHistory };
    internal void Apply(Change change)
    {
        PathRevision++;
        if (change.OpenCount > 0) _hasHistory = true;
        var parent = Find(change.Parent); var id = FindAlias(change.Frn, parent, change.Name);
        if (change.NewEntry is null) { if (id != 0) Delete(id); return; }
        var e = change.NewEntry; var nextParent = e.FileReference == Data.RootFrn ? 0 : DirectoryId(e.ParentFileReference);
        if (nextParent != 0 && !Alive(nextParent)) throw new InvalidDataException("Missing updated parent");
        if (id != 0 && IsDirectory(id)) { for (var a = nextParent; a != 0; a = (uint)Row(a).Parent) if (a == id) throw new InvalidDataException("Rename cycle"); }
        if (id == RootId) throw new InvalidDataException("Root mutation");
        if (id == 0 || Row(id).Parent != nextParent || !NameEquals(id, e.Name, StringComparison.OrdinalIgnoreCase)) foreach (var child in Children(nextParent)) if (child != id && NameEquals(child, e.Name, StringComparison.OrdinalIgnoreCase)) Delete(child);
        if (id == 0) { Accept(e, change.OpenCount); return; }
        var row = Row(id); if (row.Parent != nextParent) { row.Parent = (int)nextParent; Write(id, row); AddChild(nextParent, id); }
        if (!NameEquals(id, e.Name, StringComparison.Ordinal)) { row = Row(id); row.Name = InternName(e.Name) | Dirty; Write(id, row); AddTerms(id, e.Name); }
        if (change.OpenCount < 0) throw new InvalidDataException("Negative open count");
        for (var alias = Find(e.FileReference); alias != 0; alias = IsDirectory(alias) ? 0 : AliasNext(alias))
        { var r = Row(alias); var old = Meta(alias); var m = new Metadata((uint)e.Attributes, e.SizeBytes, e.CreatedTimeUtc, e.LastWriteTimeUtc, alias == id ? change.OpenCount : old.OpenCount); if (old with { Length = 0 } != m) { r.Meta = AppendMetadata(m); Write(alias, r); SetAlias(alias | 0x80000000, (uint)m.OpenCount); } }
    }
    void Delete(uint id)
    {
        PathRevision++;
        if (id == RootId) throw new InvalidDataException("Delete root"); var stack = new Stack<uint>(); stack.Push(id);
        while (stack.TryPop(out var current)) { if (!Alive(current)) continue; foreach (var child in Children(current)) stack.Push(child); var row = Row(current); var head = Find(row.Frn); var next = IsDirectory(current) ? 0 : AliasNext(current); if (head == current) SetFrn(row.Frn, next); else for (var a = head; a != 0; a = AliasNext(a)) if (AliasNext(a) == current) { SetAlias(a, next); break; } SetAlias(current, 0); SetAlias(current | 0x80000000, 0); row.Name = -Math.Max(1, row.Name & NameMask); row.Parent = IsDirectory(current) ? Data.DirFree : Data.FileFree; if (IsDirectory(current)) Data.DirFree = (int)current; else Data.FileFree = (int)current; Write(current, row); Data.EntryCount--; }
    }
    internal void Finish(long cursor = 0, bool buildNameOrder = true) { Audit(); if (buildNameOrder) BuildAndCommit(cursor); else Data.Commit(cursor); }
    internal void BuildAndCommit(long cursor = 0, bool checkpoint = false) { BuildNameRuns(); Data.Commit(cursor, checkpoint); }
    internal uint Resolve(NtfsMftEntry e) => e.IsDirectory && e.FileReference == Data.RootFrn ? RootId : FindAlias(e.FileReference, Find(e.ParentFileReference), e.Name);
    internal void RemoveVerifiedAlias(uint id) => Delete(id);
    internal void DropEmptyPlaceholder(ulong frn)
    { var id = Find(frn); if (id == 0 || Alive(id)) return; if (ActiveIds().Any(child => Row(child).Parent == id)) throw new InvalidDataException("Placeholder still has children"); SetFrn(frn, 0); Write(id, new FileRow { Frn = frn, Name = -1, Parent = Data.DirFree }); Data.DirFree = (int)id; }
    internal Catalog Compact()
    {
        var next = Fork(); var d = next.Data;
        for (int area = 0; area < Image.Sizes.Length; area++) d.Reset(area);
        d.ZeroRows(Image.Files, (int)Data.Count(Image.Files)); d.ZeroRows(Image.Dirs, (int)Data.Count(Image.Dirs));
        d.Append(Image.Names, new byte[1]); d.Append(Image.Metadata, new byte[1]); d.Add(Image.Words, default(Word)); d.Add(Image.Children, default(Posting)); d.Append(Image.Postings, new byte[32]);
        d.ZeroRows(Image.FileHash, d.HashSize); d.ZeroRows(Image.NameHash, d.NameHashSize); d.ZeroRows(Image.WordHash, d.WordHashSize); d.ZeroRows(Image.Aliases, d.AliasSize); d.ZeroRows(Image.Grams, 16385);
        d.NameUsed = d.AliasUsed = d.FileFree = d.DirFree = d.EntryCount = 0; next._hasHistory = false;
        foreach (var id in ActiveIds())
        { var row = Row(id); var name = Name(id); var meta = Meta(id); row.Name = next.InternName(name); row.Meta = next.AppendMetadata(meta); next.Write(id, row); next.SetAlias(id, next.Find(row.Frn)); next.SetFrn(row.Frn, id); if (meta.OpenCount != 0) { next.SetAlias(id | 0x80000000, (uint)meta.OpenCount); next._hasHistory = true; } next.AddTerms(id, name); d.EntryCount++; }
        foreach (var id in ActiveIds()) { var parent = Row(id).Parent; if (parent != 0) next.AddChild((uint)parent, id); }
        for (uint i = 1; i < d.Count(Image.Files); i++) { var id = i * 2; if (!next.Alive(id)) { next.Write(id, new FileRow { Name = -1, Parent = d.FileFree }); d.FileFree = (int)id; } }
        for (uint i = 1; i < d.Count(Image.Dirs); i++) { var id = i * 2 + 1; if (!next.Alive(id)) { next.Write(id, new FileRow { Name = -1, Parent = d.DirFree }); d.DirFree = (int)id; } }
        d.TermsReady = true; d.TokenBuildCursor = next.MaxId;
        next.Audit(); next.BuildAndCommit(Data.Cursor, true); return next;
    }
    internal Catalog Retain()
    {
        while (true)
        {
            var count = Volatile.Read(ref _references);
            ObjectDisposedException.ThrowIf(count <= 0, this);
            if (Interlocked.CompareExchange(ref _references, count + 1, count) == count) return this;
        }
    }
    public void Dispose()
    {
        if (Interlocked.Decrement(ref _references) != 0) return;
        Interlocked.Exchange(ref _literalBaseline, null)?.Dispose();
        Data.Dispose();
    }
}
