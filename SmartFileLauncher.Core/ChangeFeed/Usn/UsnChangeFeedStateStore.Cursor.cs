using System.Security.Cryptography;
using System.Text.Json;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed partial class UsnChangeFeedStateStore
{
    private sealed record Cursor(Guid SnapshotId, ulong JournalId, long NextUsn, bool PendingSecurityChange);
    private Cursor? _persistedCursor;
    private bool CanWriteCursor(ulong journalId, IReadOnlyList<UsnChangeFeedState> roots)
    {
        if (_cachedBase is null || _snapshotId == Guid.Empty || _cachedBase.JournalId != journalId || _cachedBase.Roots.Count != roots.Count) return false;
        for (var index = 0; index < roots.Count; index++)
        {
            var old = _cachedBase.Roots[index]; var next = roots[index];
            if (old.RootPath != next.RootPath || old.RootIdentity != next.RootIdentity || old.SynchronizedFromUsn != next.SynchronizedFromUsn ||
                !ReferenceEquals(old.Directories, next.Directories)) return false;
        }
        return true;
    }
    private UsnVolumeFeedState ApplyCursor(UsnVolumeFeedState baseline)
    {
        _persistedCursor = null;
        var path = _filePath + ".cursor";
        if (!File.Exists(path)) return RememberBaseline();
        var bytes = File.ReadAllBytes(path);
        if (bytes.Length < 32 || bytes.Length > 4096 || !CryptographicOperations.FixedTimeEquals(
                SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)), bytes.AsSpan(bytes.Length - 32)))
            throw new InvalidDataException("USN ilerleme kaydı checksum uyuşmuyor.");
        var cursor = JsonSerializer.Deserialize<Cursor>(bytes.AsSpan(0, bytes.Length - 32)) ?? throw new InvalidDataException("USN ilerleme kaydı boş.");
        if (cursor.SnapshotId != _snapshotId) return RememberBaseline();
        if (cursor.JournalId != baseline.JournalId || cursor.NextUsn < baseline.NextUsn) throw new InvalidDataException("USN ilerleme kaydı geriye gidiyor.");
        _persistedCursor = cursor;
        return new(cursor.JournalId, cursor.NextUsn, baseline.Roots.Select(root => root.WithPosition(cursor.JournalId, cursor.NextUsn)).ToArray(), cursor.PendingSecurityChange);

        UsnVolumeFeedState RememberBaseline()
        {
            _persistedCursor = new(_snapshotId, baseline.JournalId, baseline.NextUsn, baseline.PendingSecurityChange);
            return baseline;
        }
    }
    private void WriteCursor(Cursor cursor)
    {
        if (cursor == _persistedCursor) return;
        var bytes = JsonSerializer.SerializeToUtf8Bytes(cursor); var path = _filePath + ".cursor";
        using (var output = new FileStream(path + ".tmp", FileMode.Create, FileAccess.Write, FileShare.None))
        { output.Write(bytes); output.Write(SHA256.HashData(bytes)); output.Flush(true); }
        File.Move(path + ".tmp", path, true);
        _persistedCursor = cursor;
    }
}
