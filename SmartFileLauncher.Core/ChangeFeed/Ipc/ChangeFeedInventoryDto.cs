using System.Runtime.InteropServices;
using SmartFileLauncher.Core.Indexing.Ntfs;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed record ChangeFeedInventoryEntryDto(
    string PathUtf16,
    bool IsDirectory,
    FileAttributes Attributes,
    long SizeBytes,
    long CreatedTimeUtc,
    long LastWriteTimeUtc)
{
    public static ChangeFeedInventoryEntryDto FromEntry(IndexInventoryEntry entry) =>
        new(Convert.ToBase64String(MemoryMarshal.AsBytes(entry.Path.AsSpan())),
            entry.IsDirectory, entry.Attributes, entry.SizeBytes,
            entry.CreatedTimeUtc, entry.LastWriteTimeUtc);

    public IndexInventoryEntry ToEntry()
    {
        var bytes = Convert.FromBase64String(PathUtf16);
        if (bytes.Length == 0 || bytes.Length % 2 != 0)
            throw new InvalidDataException("Envanter yolu geçersiz.");
        return new IndexInventoryEntry(new string(MemoryMarshal.Cast<byte, char>(bytes)),
            IsDirectory, Attributes, SizeBytes, CreatedTimeUtc, LastWriteTimeUtc);
    }
}

public sealed record ChangeFeedInventoryPageDto(
    string Token,
    IReadOnlyList<ChangeFeedInventoryEntryDto> Entries,
    bool Completed);
