using System.Globalization;
using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// Everything a USN feed must persist to resume after the application closes:
/// the root identity it was built for, the journal position, and the directory
/// map that turns file identities back into paths.
/// </summary>
/// <remarks>
/// The baseline must be taken in this order: query the journal first, take the
/// directory snapshot second. Changes made during the snapshot are then
/// replayed by the first read instead of being lost.
/// </remarks>
public sealed class UsnChangeFeedState
{
    public UsnChangeFeedState(
        string rootPath,
        UsnNodeIdentity rootIdentity,
        ulong journalId,
        long nextUsn,
        IReadOnlyList<UsnDirectoryEntry> directories)
    {
        if (string.IsNullOrWhiteSpace(rootPath))
        {
            throw new ArgumentException("Kök yolu boş olamaz.", nameof(rootPath));
        }

        if (rootIdentity.FileReference.IsNone)
        {
            throw new ArgumentException("Kök kimliği boş olamaz.", nameof(rootIdentity));
        }

        ArgumentNullException.ThrowIfNull(directories);
        ArgumentOutOfRangeException.ThrowIfNegative(nextUsn);

        RootPath = Path.TrimEndingDirectorySeparator(rootPath);
        RootIdentity = rootIdentity;
        JournalId = journalId;
        NextUsn = nextUsn;
        Directories = directories;
    }

    public string RootPath { get; }

    public UsnNodeIdentity RootIdentity { get; }

    public ulong JournalId { get; }

    public long NextUsn { get; }

    public IReadOnlyList<UsnDirectoryEntry> Directories { get; }

    public ChangeFeedRootIdentity ToChangeFeedRootIdentity() =>
        new(
            string.Create(
                CultureInfo.InvariantCulture,
                $"ntfs-vsn:0x{RootIdentity.VolumeSerialNumber:X16}"),
            RootIdentity.FileReference.ToString());
}
