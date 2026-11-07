using System.Runtime.InteropServices;
using SmartFileLauncher.Core.Indexing.Ntfs;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed record ChangeFeedInventoryEntryDto(
    string PathUtf16,
    bool IsDirectory,
    FileAttributes Attributes,
    long SizeBytes,
    long CreatedTimeUtc,
    long LastWriteTimeUtc,
    [property: System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    ChangeFeedInventoryIdentityDto? Identity = null)
{
    private static readonly long MaximumMetadataSize = ChangeFeedMessageChannel.MeasureResponse(
        new ChangeFeedInventoryEntryDto(string.Empty, false, (FileAttributes)int.MinValue,
            long.MinValue, long.MinValue, long.MinValue,
            new ChangeFeedInventoryIdentityDto(string.Empty, ulong.MaxValue, ulong.MaxValue, string.Empty))) +
        Enum.GetNames<FileAttributes>().Sum(name => name.Length + 2);

    internal long EncodedSizeUpperBound => PathUtf16.Length + MaximumMetadataSize +
        (Identity is null ? 0 : Identity.VolumeId.Length * 6L + Identity.NameUtf16.Length);

    public static ChangeFeedInventoryEntryDto FromEntry(IndexInventoryEntry entry) =>
        new(Convert.ToBase64String(MemoryMarshal.AsBytes(entry.Path.AsSpan())),
            entry.IsDirectory, entry.Attributes, entry.SizeBytes,
            entry.CreatedTimeUtc, entry.LastWriteTimeUtc,
            entry.Identity is { } identity ? new(identity.VolumeId, identity.FileReference,
                identity.ParentFileReference, Convert.ToBase64String(MemoryMarshal.AsBytes(identity.Name.AsSpan()))) : null);

    public IndexInventoryEntry ToEntry()
    {
        var bytes = Convert.FromBase64String(PathUtf16);
        if (bytes.Length == 0 || bytes.Length % 2 != 0)
            throw new InvalidDataException("Envanter yolu geçersiz.");
        return new IndexInventoryEntry(new string(MemoryMarshal.Cast<byte, char>(bytes)),
            IsDirectory, Attributes, SizeBytes, CreatedTimeUtc, LastWriteTimeUtc, Identity?.ToIdentity());
    }
}

public sealed record ChangeFeedInventoryIdentityDto(
    string VolumeId, ulong FileReference, ulong ParentFileReference, string NameUtf16)
{
    public NtfsInventoryIdentity ToIdentity()
    {
        if (string.IsNullOrWhiteSpace(VolumeId)) throw new InvalidDataException("Envanter birim kimliği geçersiz.");
        var bytes = Convert.FromBase64String(NameUtf16);
        if (bytes.Length == 0 || bytes.Length % 2 != 0)
            throw new InvalidDataException("Envanter dosya adı geçersiz.");
        return new(VolumeId, FileReference, ParentFileReference, new string(MemoryMarshal.Cast<byte, char>(bytes)));
    }
}

public sealed record ChangeFeedInventoryPageDto(
    string Token,
    IReadOnlyList<ChangeFeedInventoryEntryDto> Entries,
    bool Completed);
