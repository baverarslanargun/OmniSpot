using System.Text;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

internal sealed class UsnRecordBuffer
{
    private const int V2HeaderLength = 60;
    private const int V3HeaderLength = 76;

    private readonly List<byte[]> _records = new();

    public UsnRecordBuffer AddVersion2(
        long usn,
        ulong fileReference,
        ulong parentReference,
        UsnReason reason,
        string name,
        FileAttributes attributes = FileAttributes.Normal)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var record = CreateRecord(V2HeaderLength, nameBytes, majorVersion: 2);

        BitConverter.TryWriteBytes(record.AsSpan(8), fileReference);
        BitConverter.TryWriteBytes(record.AsSpan(16), parentReference);
        BitConverter.TryWriteBytes(record.AsSpan(24), usn);
        BitConverter.TryWriteBytes(record.AsSpan(40), (uint)reason);
        BitConverter.TryWriteBytes(record.AsSpan(52), (uint)attributes);

        _records.Add(record);
        return this;
    }

    public UsnRecordBuffer AddVersion3(
        long usn,
        UsnFileReference fileReference,
        UsnFileReference parentReference,
        UsnReason reason,
        string name,
        FileAttributes attributes = FileAttributes.Normal)
    {
        var nameBytes = Encoding.Unicode.GetBytes(name);
        var record = CreateRecord(V3HeaderLength, nameBytes, majorVersion: 3);

        BitConverter.TryWriteBytes(record.AsSpan(8), fileReference.Low);
        BitConverter.TryWriteBytes(record.AsSpan(16), fileReference.High);
        BitConverter.TryWriteBytes(record.AsSpan(24), parentReference.Low);
        BitConverter.TryWriteBytes(record.AsSpan(32), parentReference.High);
        BitConverter.TryWriteBytes(record.AsSpan(40), usn);
        BitConverter.TryWriteBytes(record.AsSpan(56), (uint)reason);
        BitConverter.TryWriteBytes(record.AsSpan(68), (uint)attributes);

        _records.Add(record);
        return this;
    }

    public byte[] Build() => _records.SelectMany(record => record).ToArray();

    private static byte[] CreateRecord(int headerLength, byte[] nameBytes, ushort majorVersion)
    {
        var unpaddedLength = headerLength + nameBytes.Length;
        var recordLength = (unpaddedLength + 7) / 8 * 8;
        var record = new byte[recordLength];

        BitConverter.TryWriteBytes(record.AsSpan(0), (uint)recordLength);
        BitConverter.TryWriteBytes(record.AsSpan(4), majorVersion);
        BitConverter.TryWriteBytes(record.AsSpan(6), (ushort)0);
        BitConverter.TryWriteBytes(record.AsSpan(headerLength - 4), (ushort)nameBytes.Length);
        BitConverter.TryWriteBytes(record.AsSpan(headerLength - 2), (ushort)headerLength);
        nameBytes.CopyTo(record.AsSpan(headerLength));

        return record;
    }
}
