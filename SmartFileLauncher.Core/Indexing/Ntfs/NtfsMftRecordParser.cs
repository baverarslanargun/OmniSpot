using System.Buffers.Binary;
using System.Runtime.InteropServices;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

internal static class NtfsMftRecordParser
{
    private const ulong SegmentMask = 0x0000FFFFFFFFFFFF;

    public static NtfsMftRecord? Parse(
        Span<byte> record,
        ulong segmentNumber,
        bool restoreSectorTails,
        bool includeDataRuns = false)
    {
        if (segmentNumber > SegmentMask || record.Length < 48)
            throw Invalid("Kayıt başlığı veya segment numarası geçersiz.");
        if (record.IndexOfAnyExcept((byte)0) < 0)
            return null;
        if (!record[..4].SequenceEqual("FILE"u8))
            throw Invalid("FILE imzası bulunamadı.");

        var flags = Read16(record, 22);
        if ((flags & 1) == 0)
            return null;
        if (restoreSectorTails)
            RestoreSectorTails(record);

        var used = Read32(record, 24);
        var firstAttribute = Read16(record, 20);
        if (used > record.Length || used < 48 || firstAttribute < 48 ||
            firstAttribute % 8 != 0 || firstAttribute > used - 4)
            throw Invalid("Öznitelik alanı kayıt sınırının dışında.");
        var sequence = Read16(record, 16);
        if (sequence == 0)
            throw Invalid("Kullanımdaki kaydın sıra numarası sıfır.");

        var result = new NtfsMftRecord
        {
            Reference = segmentNumber | ((ulong)sequence << 48),
            BaseReference = Read64(record, 32),
            IsDirectory = (flags & 2) != 0
        };
        var offset = (int)firstAttribute;
        while (offset <= used - 4)
        {
            var type = Read32(record, offset);
            if (type == uint.MaxValue)
                return result;
            if (used - offset < 16)
                throw Invalid("Öznitelik başlığı tamamlanmadı.");
            var length = Read32(record, offset + 4);
            if (length < 24 || length % 8 != 0 || length > used - offset)
                throw Invalid("Öznitelik uzunluğu geçersiz.");
            var attribute = record.Slice(offset, (int)length);
            ReadAttribute(attribute, type, result, includeDataRuns);
            offset += (int)length;
        }

        throw Invalid("Öznitelik bitiş işareti bulunamadı.");
    }

    public static IReadOnlyList<NtfsDataRun> ParseDataRuns(
        ReadOnlySpan<byte> bytes, long lowestVcn, long highestVcn)
    {
        if (lowestVcn < 0 || highestVcn < lowestVcn)
            throw Invalid("VCN aralığı geçersiz.");
        var runs = new List<NtfsDataRun>();
        var offset = 0;
        var vcn = lowestVcn;
        long lcn = 0;
        try
        {
            while (offset < bytes.Length)
            {
                var header = bytes[offset++];
                if (header == 0)
                {
                    if (vcn != checked(highestVcn + 1))
                        throw Invalid("Veri parçaları VCN aralığını tamamlamıyor.");
                    return runs;
                }
                var countBytes = header & 15;
                var offsetBytes = header >> 4;
                if (countBytes is < 1 or > 8 || offsetBytes > 8 ||
                    countBytes + offsetBytes > bytes.Length - offset)
                    throw Invalid("Veri parçası kodlaması geçersiz.");
                var count = ReadUnsigned(bytes.Slice(offset, countBytes));
                offset += countBytes;
                if (count == 0 || count > long.MaxValue)
                    throw Invalid("Veri parçasının uzunluğu geçersiz.");
                long? physical = null;
                if (offsetBytes > 0)
                {
                    var delta = ReadSigned(bytes.Slice(offset, offsetBytes));
                    lcn = checked(lcn + delta);
                    if (lcn < 0)
                        throw Invalid("Veri parçasının fiziksel adresi negatif.");
                    physical = lcn;
                }
                offset += offsetBytes;
                runs.Add(new NtfsDataRun(vcn, (long)count, physical));
                vcn = checked(vcn + (long)count);
                if (vcn - 1 > highestVcn)
                    throw Invalid("Veri parçası VCN aralığını aşıyor.");
            }
        }
        catch (OverflowException exception)
        {
            throw new InvalidDataException("MFT veri parçası adresi taştı.", exception);
        }

        throw Invalid("Veri parçası bitiş işareti bulunamadı.");
    }

    public static IReadOnlyList<ulong> ReadAttributeReferences(ReadOnlySpan<byte> bytes)
    {
        var references = new HashSet<ulong>();
        var offset = 0;
        while (offset < bytes.Length)
        {
            var entry = bytes[offset..];
            if (entry.IndexOfAnyExcept((byte)0) < 0)
                break;
            if (entry.Length < 26)
                throw Invalid("Öznitelik listesi girdisi tamamlanmadı.");
            var length = Read16(entry, 4);
            var nameLength = entry[6];
            var nameOffset = entry[7];
            if (length < 26 || length % 8 != 0 || length > entry.Length ||
                (nameLength > 0 && (nameOffset < 26 || nameOffset + nameLength * 2 > length)))
                throw Invalid("Öznitelik listesi girdisi geçersiz.");
            var type = Read32(entry, 0);
            if (nameLength == 0 && type is 0x10 or 0x20 or 0x30 or 0x80)
                references.Add(Read64(entry, 16));
            offset += length;
        }
        return references.ToArray();
    }

    private static void ReadAttribute(
        ReadOnlySpan<byte> attribute, uint type, NtfsMftRecord result, bool includeDataRuns)
    {
        var nonresident = attribute[8];
        if (nonresident > 1)
            throw Invalid("Öznitelik biçimi geçersiz.");
        var headerLength = nonresident == 0 ? 24 : 64;
        var nameLength = attribute[9];
        var nameOffset = Read16(attribute, 10);
        if (attribute.Length < headerLength ||
            (nameLength > 0 && (nameOffset < headerLength || nameOffset + nameLength * 2 > attribute.Length)))
            throw Invalid("Öznitelik adı veya başlığı kayıt sınırının dışında.");
        if (nameLength > 0)
            return;

        if (nonresident == 0)
        {
            var valueLength = Read32(attribute, 16);
            var valueOffset = Read16(attribute, 20);
            if (valueOffset < headerLength || valueOffset > attribute.Length ||
                valueLength > attribute.Length - valueOffset)
                throw Invalid("Yerleşik öznitelik değeri kayıt sınırının dışında.");
            var value = attribute.Slice(valueOffset, (int)valueLength);
            switch (type)
            {
                case 0x10:
                    if (value.Length < 36)
                        throw Invalid("STANDARD_INFORMATION tamamlanmadı.");
                    result.BasicInformation = new NtfsBasicInformation(
                        ReadFileTime(value, 0), ReadFileTime(value, 8),
                        (FileAttributes)Read32(value, 32));
                    break;
                case 0x20:
                    result.ResidentAttributeList = value.ToArray();
                    break;
                case 0x30:
                    if (value.Length < 66 || value[64] == 0 ||
                        66 + value[64] * 2 > value.Length || value[65] > 3)
                        throw Invalid("FILE_NAME tamamlanmadı veya geçersiz.");
                    var name = new string(MemoryMarshal.Cast<byte, char>(value.Slice(66, value[64] * 2)));
                    if (name.AsSpan().IndexOfAny('\0', '\\', '/') >= 0)
                        throw Invalid("FILE_NAME tek bir yol bileşeni değil.");
                    result.Names.Add(new NtfsFileName(Read64(value, 0), name, value[65]));
                    break;
                case 0x80:
                    result.SizeBytes = valueLength;
                    break;
            }
            return;
        }

        if (type is not (0x20 or 0x80))
            return;
        var lowestVcn = ReadInt64(attribute, 16);
        var highestVcn = ReadInt64(attribute, 24);
        var length = ReadInt64(attribute, 48);
        if (lowestVcn < 0 || (lowestVcn == 0 && length < 0))
            throw Invalid("Yerleşik olmayan öznitelik boyutu geçersiz.");
        if (type == 0x80 && lowestVcn == 0)
            result.SizeBytes = length;
        if (type == 0x80 && !includeDataRuns)
            return;
        if ((Read16(attribute, 12) & 0x40FF) != 0)
            throw new NotSupportedException("Sıkıştırılmış veya şifreli MFT metadata akışı desteklenmiyor.");
        var runOffset = Read16(attribute, 32);
        if (runOffset < headerLength || runOffset >= attribute.Length)
            throw Invalid("Veri parçalarının konumu geçersiz.");
        var extent = new NtfsDataExtent(lowestVcn, length,
            ParseDataRuns(attribute[runOffset..], lowestVcn, highestVcn));
        if (type == 0x20)
        {
            if (lowestVcn != 0)
                throw new NotSupportedException("Çok segmentli öznitelik listesi desteklenmiyor.");
            result.NonresidentAttributeList = extent;
        }
        else
            result.DataExtents.Add(extent);
    }

    private static void RestoreSectorTails(Span<byte> record)
    {
        const int stride = 512;
        var offset = Read16(record, 4);
        var count = Read16(record, 6);
        if (record.Length % stride != 0 || count != record.Length / stride + 1 ||
            offset < 8 || offset % 2 != 0 || offset + count * 2 > stride - 2)
            throw Invalid("Sektör düzeltme dizisi geçersiz.");
        var sequence = Read16(record, offset);
        for (var index = 1; index < count; index++)
        {
            if (Read16(record, index * stride - 2) != sequence)
                throw Invalid("MFT kaydında yarım kalmış sektör yazımı algılandı.");
        }
        for (var index = 1; index < count; index++)
            BinaryPrimitives.WriteUInt16LittleEndian(record.Slice(index * stride - 2, 2),
                Read16(record, offset + index * 2));
    }

    private static long ReadFileTime(ReadOnlySpan<byte> bytes, int offset)
    {
        try { return DateTime.FromFileTimeUtc(ReadInt64(bytes, offset)).Ticks; }
        catch (ArgumentOutOfRangeException exception)
        { throw new InvalidDataException("MFT zaman damgası geçersiz.", exception); }
    }

    private static ulong ReadUnsigned(ReadOnlySpan<byte> bytes)
    {
        ulong value = 0;
        for (var index = 0; index < bytes.Length; index++)
            value |= (ulong)bytes[index] << (index * 8);
        return value;
    }

    private static long ReadSigned(ReadOnlySpan<byte> bytes)
    {
        var value = ReadUnsigned(bytes);
        if (bytes.Length < 8 && (bytes[^1] & 0x80) != 0)
            value |= ulong.MaxValue << (bytes.Length * 8);
        return unchecked((long)value);
    }

    private static ushort Read16(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt16LittleEndian(bytes[offset..]);
    private static uint Read32(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt32LittleEndian(bytes[offset..]);
    private static ulong Read64(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadUInt64LittleEndian(bytes[offset..]);
    private static long ReadInt64(ReadOnlySpan<byte> bytes, int offset) =>
        BinaryPrimitives.ReadInt64LittleEndian(bytes[offset..]);
    private static InvalidDataException Invalid(string message) => new("MFT: " + message);
}
