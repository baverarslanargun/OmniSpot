using System.IO;
using System.Text;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public static class UsnRecordParser
{
    private const int V2HeaderLength = 60;
    private const int V3HeaderLength = 76;

    public static void Parse(ReadOnlySpan<byte> buffer, ICollection<UsnRecord> destination)
    {
        ArgumentNullException.ThrowIfNull(destination);

        var offset = 0;
        while (offset < buffer.Length)
        {
            offset += ParseRecord(buffer[offset..], destination);
        }
    }

    public static IReadOnlyList<UsnRecord> Parse(ReadOnlySpan<byte> buffer)
    {
        var records = new List<UsnRecord>();
        Parse(buffer, records);
        return records;
    }

    private static int ParseRecord(ReadOnlySpan<byte> record, ICollection<UsnRecord> destination)
    {
        if (record.Length < sizeof(uint) + sizeof(ushort))
        {
            throw new UsnRecordFormatException(
                $"USN tamponu {record.Length} baytta bitti; kayıt başlığı tamamlanmadı.");
        }

        var recordLength = BitConverter.ToUInt32(record);
        var majorVersion = BitConverter.ToUInt16(record[4..]);

        var headerLength = majorVersion switch
        {
            2 => V2HeaderLength,
            3 => V3HeaderLength,
            _ => throw new UsnRecordFormatException(
                $"Desteklenmeyen USN kayıt sürümü: {majorVersion}.")
        };

        if (recordLength < headerLength || recordLength > record.Length)
        {
            throw new UsnRecordFormatException(
                $"Geçersiz USN kayıt uzunluğu: {recordLength} (tampon kalanı {record.Length}).");
        }

        var body = record[..(int)recordLength];
        var nameLength = BitConverter.ToUInt16(body[(headerLength - 4)..]);
        var nameOffset = BitConverter.ToUInt16(body[(headerLength - 2)..]);

        if (nameOffset < headerLength ||
            nameLength % 2 != 0 ||
            (long)nameOffset + nameLength > recordLength)
        {
            throw new UsnRecordFormatException(
                $"USN kayıt adı kayıt sınırının dışında: offset={nameOffset}, length={nameLength}, " +
                $"record={recordLength}.");
        }

        var name = Encoding.Unicode.GetString(body.Slice(nameOffset, nameLength));

        destination.Add(majorVersion == 2
            ? ReadVersion2(body, name)
            : ReadVersion3(body, name));

        return (int)recordLength;
    }

    private static UsnRecord ReadVersion2(ReadOnlySpan<byte> body, string name) =>
        new(
            BitConverter.ToInt64(body[24..]),
            UsnFileReference.FromNtfs(BitConverter.ToUInt64(body[8..])),
            UsnFileReference.FromNtfs(BitConverter.ToUInt64(body[16..])),
            (UsnReason)BitConverter.ToUInt32(body[40..]),
            (FileAttributes)BitConverter.ToUInt32(body[52..]),
            name);

    private static UsnRecord ReadVersion3(ReadOnlySpan<byte> body, string name) =>
        new(
            BitConverter.ToInt64(body[40..]),
            UsnFileReference.FromFileId128(body.Slice(8, 16)),
            UsnFileReference.FromFileId128(body.Slice(24, 16)),
            (UsnReason)BitConverter.ToUInt32(body[56..]),
            (FileAttributes)BitConverter.ToUInt32(body[68..]),
            name);
}
