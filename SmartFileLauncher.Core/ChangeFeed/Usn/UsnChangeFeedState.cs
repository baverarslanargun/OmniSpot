using System.IO;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed class UsnChangeFeedState
{
    private UsnChangeFeedState(UsnChangeFeedState source, ulong journalId, long nextUsn)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(nextUsn);
        RootPath = source.RootPath; RootIdentity = source.RootIdentity; Directories = source.Directories;
        SynchronizedFromUsn = source.SynchronizedFromUsn; JournalId = journalId; NextUsn = nextUsn;
    }
    internal UsnChangeFeedState WithPosition(ulong journalId, long nextUsn) => new(this, journalId, nextUsn);
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
