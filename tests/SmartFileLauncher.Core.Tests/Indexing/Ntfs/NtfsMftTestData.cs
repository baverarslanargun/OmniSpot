using System.Buffers.Binary;
using System.Runtime.InteropServices;
using SmartFileLauncher.Core.Indexing.Ntfs;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

internal static class NtfsMftTestData
{
    public const int RecordSize = 1024;
    public static readonly DateTime Created = new(2024, 2, 3, 4, 5, 6, DateTimeKind.Utc);
    public static readonly DateTime Modified = Created.AddTicks(1234567);
    public static ulong Reference(int number, ushort sequence = 1) => ((ulong)sequence << 48) | (uint)number;

    public static byte[] Record(params byte[][] attributes) => Record(false, 0, attributes);

    public static byte[] Record(bool directory, ulong baseReference, params byte[][] attributes)
    {
        var bytes = new byte[RecordSize];
        "FILE"u8.CopyTo(bytes);
        Write16(bytes, 4, 48);
        Write16(bytes, 6, 3);
        Write16(bytes, 16, 1);
        Write16(bytes, 20, 56);
        Write16(bytes, 22, directory ? 3 : 1);
        Write32(bytes, 28, RecordSize);
        Write64(bytes, 32, baseReference);
        var offset = 56;
        foreach (var attribute in attributes)
        {
            attribute.CopyTo(bytes, offset);
            offset += attribute.Length;
        }
        Write32(bytes, offset, uint.MaxValue);
        Write32(bytes, 24, (uint)(offset + 8));
        return bytes;
    }

    public static byte[] Protect(byte[] decoded)
    {
        var bytes = decoded.ToArray();
        Write16(bytes, 48, 0xA73C);
        Write16(bytes, 50, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(510)));
        Write16(bytes, 52, BinaryPrimitives.ReadUInt16LittleEndian(bytes.AsSpan(1022)));
        Write16(bytes, 510, 0xA73C);
        Write16(bytes, 1022, 0xA73C);
        return bytes;
    }

    public static byte[] Basic(FileAttributes attributes = FileAttributes.Archive)
    {
        var value = new byte[72];
        Write64(value, 0, (ulong)Created.ToFileTimeUtc());
        Write64(value, 8, (ulong)Modified.ToFileTimeUtc());
        Write32(value, 32, (uint)attributes);
        return Resident(0x10, value);
    }

    public static byte[] Name(string name, int parent = 5, byte nameSpace = 1)
    {
        var value = new byte[66 + name.Length * 2];
        Write64(value, 0, Reference(parent));
        value[64] = checked((byte)name.Length);
        value[65] = nameSpace;
        MemoryMarshal.AsBytes(name.AsSpan()).CopyTo(value.AsSpan(66));
        return Resident(0x30, value);
    }

    public static byte[] Resident(uint type, byte[] value)
    {
        var bytes = new byte[Align(24 + value.Length)];
        Write32(bytes, 0, type);
        Write32(bytes, 4, (uint)bytes.Length);
        Write32(bytes, 16, (uint)value.Length);
        Write16(bytes, 20, 24);
        value.CopyTo(bytes, 24);
        return bytes;
    }

    public static byte[] Nonresident(
        uint type, long length, long lowVcn, long highVcn, params byte[] runs)
    {
        var bytes = new byte[Align(64 + runs.Length)];
        Write32(bytes, 0, type);
        Write32(bytes, 4, (uint)bytes.Length);
        bytes[8] = 1;
        Write64(bytes, 16, (ulong)lowVcn);
        Write64(bytes, 24, (ulong)highVcn);
        Write16(bytes, 32, 64);
        Write64(bytes, 48, (ulong)length);
        runs.CopyTo(bytes, 64);
        return bytes;
    }

    public static byte[] AttributeList(params (uint Type, ulong Reference)[] entries)
    {
        var bytes = new byte[entries.Length * 32];
        for (var index = 0; index < entries.Length; index++)
        {
            var offset = index * 32;
            Write32(bytes, offset, entries[index].Type);
            Write16(bytes, offset + 4, 32);
            Write64(bytes, offset + 16, entries[index].Reference);
        }
        return Resident(0x20, bytes);
    }

    public static void Write16(byte[] bytes, int offset, int value) =>
        BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(offset), checked((ushort)value));
    public static void Write32(byte[] bytes, int offset, uint value) =>
        BinaryPrimitives.WriteUInt32LittleEndian(bytes.AsSpan(offset), value);
    public static void Write64(byte[] bytes, int offset, ulong value) =>
        BinaryPrimitives.WriteUInt64LittleEndian(bytes.AsSpan(offset), value);
    private static int Align(int length) => (length + 7) & ~7;
}

internal sealed class FakeNtfsVolumeSource : INtfsVolumeSource
{
    private readonly byte[] _volume = new byte[128 * 512];
    private readonly Dictionary<ulong, byte[]> _records = [];
    public NtfsVolumeGeometry Geometry { get; set; } = new(512, 512, 1024, 128, 4096);
    public List<(long Offset, int Count)> Reads { get; } = [];
    public List<ulong> RecordReads { get; } = [];
    public bool Disposed { get; private set; }

    public void Add(int recordNumber, byte[] record, long physicalOffset)
    {
        _records[(ulong)recordNumber] = record;
        NtfsMftTestData.Protect(record).CopyTo(_volume, checked((int)physicalOffset));
    }

    public void CorruptByte(long physicalOffset) => _volume[checked((int)physicalOffset)] ^= 1;

    public void RemoveKernelRecord(int number) => _records.Remove((ulong)number);

    public byte[]? ReadFileRecord(ulong reference)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        RecordReads.Add(reference);
        return _records.TryGetValue(reference & 0x0000FFFFFFFFFFFF, out var record)
            ? record.ToArray() : null;
    }

    public void ReadAt(long offset, Span<byte> destination)
    {
        ObjectDisposedException.ThrowIf(Disposed, this);
        Reads.Add((offset, destination.Length));
        _volume.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
    }

    public void Dispose() => Disposed = true;
}
