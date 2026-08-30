using System.Globalization;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// A 128-bit file identity. NTFS uses only the low 64 bits; ReFS records
/// (<c>USN_RECORD_V3</c>) use the full width.
/// </summary>
public readonly record struct UsnFileReference(ulong Low, ulong High)
{
    public static readonly UsnFileReference None = default;

    public bool IsNone => Low == 0 && High == 0;

    public static UsnFileReference FromNtfs(ulong fileReferenceNumber) =>
        new(fileReferenceNumber, 0);

    public static UsnFileReference FromFileId128(ReadOnlySpan<byte> identifier)
    {
        if (identifier.Length < 16)
        {
            throw new ArgumentException(
                "FILE_ID_128 için 16 bayt gereklidir.",
                nameof(identifier));
        }

        return new UsnFileReference(
            BitConverter.ToUInt64(identifier[..8]),
            BitConverter.ToUInt64(identifier.Slice(8, 8)));
    }

    public override string ToString() =>
        High == 0
            ? string.Create(CultureInfo.InvariantCulture, $"0x{Low:X16}")
            : string.Create(CultureInfo.InvariantCulture, $"0x{High:X16}{Low:X16}");
}
