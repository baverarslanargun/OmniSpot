using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed class UsnChangeFeedState
{
    public UsnChangeFeedState(
        string rootPath,
        UsnNodeIdentity rootIdentity,
        ulong journalId,
        long nextUsn,
        IReadOnlyList<UsnDirectoryEntry> directories,
        long synchronizedFromUsn = 0)
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
        ArgumentOutOfRangeException.ThrowIfNegative(synchronizedFromUsn);

        foreach (var entry in directories)
        {
            if (!UsnDirectoryNames.IsSingleSegment(entry.Name))
            {
                throw new ArgumentException(
                    $"Dizin adı tek bir ad parçası olmalıdır: {entry.Name}",
                    nameof(directories));
            }
        }

        RootPath = Path.TrimEndingDirectorySeparator(rootPath);
        RootIdentity = rootIdentity;
        JournalId = journalId;
        NextUsn = nextUsn;
        Directories = directories;
        SynchronizedFromUsn = synchronizedFromUsn;
    }

    public string RootPath { get; }

    public UsnNodeIdentity RootIdentity { get; }

    public ulong JournalId { get; }

    public long NextUsn { get; }

    public IReadOnlyList<UsnDirectoryEntry> Directories { get; }

    public long SynchronizedFromUsn { get; }

    public ChangeFeedRootIdentity ToChangeFeedRootIdentity() =>
        RootIdentity.ToChangeFeedRootIdentity();
}
