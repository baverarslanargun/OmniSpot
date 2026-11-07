using System.Buffers.Binary;
using System.IO;
using System.Security.Cryptography;
using System.Text.Json;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed partial class UsnChangeFeedStateStore
{
    private const long MinimumDeltaBudget = 1024 * 1024;
    private const int FrameHashLength = 32;
    private long _deltaBytes, _deltaFileLength;

    private string DeltaPath => _filePath + ".delta";

    private long DeltaFileLength()
    {
        var delta = new FileInfo(DeltaPath);
        return delta.Exists ? delta.Length : 0;
    }

    private UsnVolumeFeedState RememberPosition(UsnVolumeFeedState current)
    {
        _persistedCursor = new(_snapshotId, current.JournalId, current.NextUsn, current.PendingSecurityChange);
        return current;
    }

    private bool SameRoots(ulong journalId, IReadOnlyList<UsnChangeFeedState> roots)
    {
        if (_cachedBase is null || _snapshotId == Guid.Empty || _cachedBase.JournalId != journalId || _cachedBase.Roots.Count != roots.Count) return false;
        for (var index = 0; index < roots.Count; index++)
        {
            var old = _cachedBase.Roots[index]; var next = roots[index];
            if (old.RootPath != next.RootPath || old.RootIdentity != next.RootIdentity || old.SynchronizedFromUsn != next.SynchronizedFromUsn) return false;
        }
        return true;
    }

    private bool CanWriteDelta(ulong journalId, IReadOnlyList<UsnChangeFeedState> roots) => SameRoots(journalId, roots);

    private static List<DeltaRootDocument> Diff(IReadOnlyList<UsnChangeFeedState> previousRoots, IReadOnlyList<UsnChangeFeedState> roots)
    {
        var changes = new List<DeltaRootDocument>(roots.Count);
        for (var index = 0; index < roots.Count; index++)
        {
            var root = new DeltaRootDocument { RootPath = roots[index].RootPath };
            changes.Add(root);
            if (ReferenceEquals(previousRoots[index].Directories, roots[index].Directories)) continue;
            var previous = new Dictionary<UsnFileReference, UsnDirectoryEntry>(previousRoots[index].Directories.Count);
            foreach (var entry in previousRoots[index].Directories) previous[entry.Reference] = entry;
            foreach (var entry in roots[index].Directories)
            {
                if (previous.Remove(entry.Reference, out var old) && old == entry) continue;
                root.Set.Add(new DirectoryDocument
                {
                    Low = entry.Reference.Low,
                    High = entry.Reference.High,
                    Name = entry.Name,
                    ParentLow = entry.ParentReference.Low,
                    ParentHigh = entry.ParentReference.High
                });
            }
            foreach (var reference in previous.Keys) root.Removed.Add(new ReferenceDocument { Low = reference.Low, High = reference.High });
        }
        return changes;
    }

    private bool TryAppendDelta(Cursor cursor, IReadOnlyList<UsnChangeFeedState> roots, List<DeltaRootDocument> changes)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(new DeltaDocument
        {
            SnapshotId = cursor.SnapshotId,
            JournalId = cursor.JournalId,
            NextUsn = cursor.NextUsn,
            PendingSecurityChange = cursor.PendingSecurityChange,
            Roots = changes
        }, SerializerOptions);
        var frameLength = sizeof(int) + payload.Length + (long)FrameHashLength;
        if (_deltaBytes + frameLength > Math.Max(MinimumDeltaBudget, _baseLength / 4)) return false;
        var prefix = new byte[sizeof(int)];
        BinaryPrimitives.WriteInt32LittleEndian(prefix, payload.Length);
        using (var output = new FileStream(DeltaPath, FileMode.OpenOrCreate, FileAccess.Write, FileShare.Read))
        {
            output.SetLength(_deltaBytes);
            output.Position = _deltaBytes;
            output.Write(prefix);
            output.Write(payload);
            output.Write(SHA256.HashData(payload));
            output.Flush(true);
        }
        if (_deltaBytes == 0) File.Delete(_filePath + ".cursor");
        _deltaBytes += frameLength; _deltaFileLength = _deltaBytes;
        _cachedBase = new(cursor.JournalId, cursor.NextUsn, roots, cursor.PendingSecurityChange);
        _persistedCursor = cursor;
        return true;
    }

    private UsnVolumeFeedState ApplyDelta(UsnVolumeFeedState baseline)
    {
        _deltaBytes = 0;
        _deltaFileLength = DeltaFileLength();
        if (_deltaFileLength == 0) return baseline;
        var bytes = File.ReadAllBytes(DeltaPath);
        _deltaFileLength = bytes.Length;
        Dictionary<UsnFileReference, UsnDirectoryEntry>[]? maps = null;
        var journalId = baseline.JournalId; var nextUsn = baseline.NextUsn; var pending = baseline.PendingSecurityChange;
        var offset = 0;
        while (TryReadFrame(bytes, offset, baseline, out var frame, out var size))
        {
            if (frame.Roots.Count != 0)
            {
                maps ??= baseline.Roots.Select(root =>
                {
                    var map = new Dictionary<UsnFileReference, UsnDirectoryEntry>(root.Directories.Count);
                    foreach (var entry in root.Directories) map[entry.Reference] = entry;
                    return map;
                }).ToArray();
                for (var index = 0; index < frame.Roots.Count; index++)
                {
                    var map = maps[index];
                    foreach (var removed in frame.Roots[index].Removed) map.Remove(new UsnFileReference(removed.Low, removed.High));
                    foreach (var set in frame.Roots[index].Set)
                    {
                        var reference = new UsnFileReference(set.Low, set.High);
                        map[reference] = new UsnDirectoryEntry(reference, set.Name, new UsnFileReference(set.ParentLow, set.ParentHigh));
                    }
                }
            }
            journalId = frame.JournalId; nextUsn = frame.NextUsn; pending = frame.PendingSecurityChange;
            offset += size;
        }
        _deltaBytes = offset;
        if (offset == 0) return baseline;
        var roots = baseline.Roots.Select((root, index) => new UsnChangeFeedState(root.RootPath, root.RootIdentity, journalId, nextUsn,
            maps is null ? root.Directories : maps[index].Values.ToArray(), root.SynchronizedFromUsn)).ToArray();
        return new UsnVolumeFeedState(journalId, nextUsn, roots, pending);
    }

    private bool TryReadFrame(byte[] bytes, int offset, UsnVolumeFeedState baseline, out DeltaDocument frame, out int size)
    {
        frame = null!; size = 0;
        if (bytes.Length - offset < sizeof(int) + FrameHashLength) return false;
        var length = BinaryPrimitives.ReadInt32LittleEndian(bytes.AsSpan(offset));
        if (length <= 0 || length > bytes.Length - offset - sizeof(int) - FrameHashLength) return false;
        var payload = bytes.AsSpan(offset + sizeof(int), length);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(payload), bytes.AsSpan(offset + sizeof(int) + length, FrameHashLength))) return false;
        DeltaDocument? document;
        try { document = JsonSerializer.Deserialize<DeltaDocument>(payload, SerializerOptions); }
        catch (JsonException) { return false; }
        if (document is null || document.SnapshotId != _snapshotId || document.JournalId != baseline.JournalId || document.NextUsn < 0 ||
            document.Roots is null) return false;
        if (document.Roots.Count != 0)
        {
            if (document.Roots.Count != baseline.Roots.Count) return false;
            for (var index = 0; index < document.Roots.Count; index++)
            {
                var root = document.Roots[index];
                if (root is null || root.RootPath != baseline.Roots[index].RootPath || root.Set is null || root.Removed is null ||
                    root.Set.Any(entry => entry is null) || root.Removed.Any(entry => entry is null)) return false;
            }
        }
        frame = document; size = sizeof(int) + length + FrameHashLength;
        return true;
    }

    private sealed class DeltaDocument
    {
        public Guid SnapshotId { get; set; }

        public ulong JournalId { get; set; }

        public long NextUsn { get; set; }

        public bool PendingSecurityChange { get; set; }

        public List<DeltaRootDocument> Roots { get; set; } = new();
    }

    private sealed class DeltaRootDocument
    {
        public string RootPath { get; set; } = string.Empty;

        public List<DirectoryDocument> Set { get; set; } = new();

        public List<ReferenceDocument> Removed { get; set; } = new();
    }

    private sealed class ReferenceDocument
    {
        public ulong Low { get; set; }

        public ulong High { get; set; }
    }
}
