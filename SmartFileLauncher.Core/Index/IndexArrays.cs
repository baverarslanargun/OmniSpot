using System.Runtime.InteropServices;

namespace SmartFileLauncher.Core.Index;

internal static class IndexArrays
{
    internal static void Write<T>(BinaryWriter writer, Span<T> values) where T : unmanaged =>
        writer.Write(MemoryMarshal.AsBytes(values));

    internal static T[] Read<T>(BinaryReader reader, int count, int capacity) where T : unmanaged
    {
        if (count < 0 || capacity < count) throw new InvalidDataException("Dizi uzunluğu geçersiz.");
        var values = new T[capacity];
        var bytes = MemoryMarshal.AsBytes(values.AsSpan(0, count));
        if (reader.BaseStream.ReadAtLeast(bytes, bytes.Length, throwOnEndOfStream: false) != bytes.Length)
            throw new InvalidDataException("İndeks dosyası beklenenden kısa.");
        return values;
    }

    internal static byte[] ReadBytes(BinaryReader reader, int count, int capacity) => Read<byte>(reader, count, capacity);

    internal static void Grow<T>(ref T[] values, int required)
    {
        if (required <= values.Length) return;
        var size = Math.Max(1024, values.Length);
        while (size < required) size = checked(size + Math.Max(size / 2, 1024));
        Array.Resize(ref values, size);
    }
}

internal sealed class ChecksumStream(Stream inner) : Stream
{
    private ulong _hash = 0x9E3779B97F4A7C15UL;
    private ulong _pending;
    private int _pendingCount;

    internal ulong Hash => _pendingCount == 0 ? _hash : Step(_hash, _pending | (ulong)_pendingCount << 56);

    private static ulong Step(ulong hash, ulong word)
    {
        hash = (hash ^ word) * 0x100000001B3UL;
        return hash ^ hash >> 29;
    }

    private void Mix(ReadOnlySpan<byte> data)
    {
        var hash = _hash;
        while (_pendingCount != 0 && !data.IsEmpty)
        {
            _pending |= (ulong)data[0] << (_pendingCount * 8);
            data = data[1..];
            if (++_pendingCount == 8)
            {
                hash = Step(hash, _pending);
                _pending = 0;
                _pendingCount = 0;
            }
        }
        while (data.Length >= 8)
        {
            hash = Step(hash, MemoryMarshal.Read<ulong>(data));
            data = data[8..];
        }
        foreach (var value in data) _pending |= (ulong)value << (_pendingCount++ * 8);
        _hash = hash;
    }

    public override void Write(byte[] buffer, int offset, int count) => Write(buffer.AsSpan(offset, count));
    public override void Write(ReadOnlySpan<byte> buffer) { Mix(buffer); inner.Write(buffer); }
    public override int Read(byte[] buffer, int offset, int count) => Read(buffer.AsSpan(offset, count));
    public override int Read(Span<byte> buffer) { var read = inner.Read(buffer); Mix(buffer[..read]); return read; }
    public override void Flush() => inner.Flush();
    public override bool CanRead => inner.CanRead;
    public override bool CanSeek => false;
    public override bool CanWrite => inner.CanWrite;
    public override long Length => inner.Length;
    public override long Position { get => inner.Position; set => throw new NotSupportedException(); }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
}
