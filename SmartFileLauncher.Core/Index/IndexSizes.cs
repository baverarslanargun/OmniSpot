using System.Buffers.Binary;

namespace SmartFileLauncher.Core.Index;

internal sealed class IndexSizes
{
    private const int Width = 5;
    private const int Padding = 3;
    private const long Limit = (1L << 40) - 1;
    private byte[] _bytes;
    private readonly Dictionary<uint, long> _large = [];

    internal IndexSizes(int capacity) => _bytes = new byte[Length(capacity)];

    private static int Length(int capacity) => checked(capacity * Width + Padding);

    internal int Capacity => (_bytes.Length - Padding) / Width;

    internal long this[uint row]
    {
        get
        {
            var value = (long)(BinaryPrimitives.ReadUInt64LittleEndian(_bytes.AsSpan((int)row * Width, 8)) & (ulong)Limit);
            return value == Limit && _large.TryGetValue(row, out var large) ? large : value;
        }
        set
        {
            if ((ulong)value < (ulong)Limit)
            {
                if (_large.Count != 0) _large.Remove(row);
            }
            else
            {
                _large[row] = value;
                value = Limit;
            }
            var target = _bytes.AsSpan((int)row * Width, Width);
            BinaryPrimitives.WriteUInt32LittleEndian(target, (uint)value);
            target[4] = (byte)(value >> 32);
        }
    }

    internal void Grow(int required)
    {
        var capacity = Capacity;
        if (required <= capacity) return;
        var size = Math.Max(1024, capacity);
        while (size < required) size = checked(size + Math.Max(size / 2, 1024));
        Array.Resize(ref _bytes, Length(size));
    }

    internal void Resize(int capacity) => Array.Resize(ref _bytes, Length(capacity));

    internal void Write(BinaryWriter writer, int count)
    {
        writer.Write(_bytes, 0, count * Width);
        var large = _large.Where(pair => pair.Key < count).OrderBy(pair => pair.Key).ToArray();
        writer.Write(large.Length);
        foreach (var (row, size) in large)
        {
            writer.Write(row);
            writer.Write(size);
        }
    }

    internal static IndexSizes Read(BinaryReader reader, int count, int capacity)
    {
        if (count < 0 || capacity < count) throw new InvalidDataException("Dizi uzunluğu geçersiz.");
        var sizes = new IndexSizes(capacity);
        var bytes = sizes._bytes.AsSpan(0, count * Width);
        if (reader.BaseStream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) != bytes.Length)
            throw new InvalidDataException("İndeks dosyası beklenenden kısa.");
        var large = reader.ReadInt32();
        if (large < 0 || large > count) throw new InvalidDataException("Büyük dosya listesi geçersiz.");
        for (var i = 0; i < large; i++)
        {
            var row = reader.ReadUInt32();
            var size = reader.ReadInt64();
            if (row >= count) throw new InvalidDataException("Büyük dosya kaydı geçersiz.");
            sizes._large[row] = size;
        }
        return sizes;
    }
}
