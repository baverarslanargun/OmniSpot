using System.Buffers.Binary;
using System.Collections.Immutable;
using System.Runtime.InteropServices;
using System.Security.Cryptography;
using System.Text;

namespace SmartFileLauncher.Core.Search;

internal sealed record PackedMutation(string Path, PackedRecord? Record);
internal sealed record PackedCommitResult(bool Replayed, string? MaintenanceError, bool RequiresReopen);
internal enum PackedWriteStage { JournalHeader, JournalFlushed, CheckpointFlushed, CheckpointReplaced }

internal sealed class PackedCatalogStore : IDisposable
{
    private const int FrameMagic = 0x4F534A31, MaxFrameBytes = 64 * 1024 * 1024;
    private readonly string _path;
    private readonly FileStream _lease;
    private readonly object _gate = new();
    private readonly ITokenizer _tokenizer = new BasicTokenizer();
    private View _view;
    private bool _poisoned, _disposed;
    internal int CheckpointDeltaLimit { get; init; } = 8192;
    private sealed record View(string FilePath, PackedCatalog Catalog, CompactSearchState State,
        ImmutableDictionary<string, PackedRecord?> Delta, PackedCheckpoint Checkpoint);
    internal CompactSearchState State => Volatile.Read(ref _view).State;
    internal PackedCheckpoint Position => Volatile.Read(ref _view).Checkpoint;
    internal string ActivePath => Volatile.Read(ref _view).FilePath;
    internal Action<PackedWriteStage>? FaultPoint { get; set; }

    internal PackedCatalogStore(string path)
    {
        _path = Path.GetFullPath(path);
        _lease = new FileStream(_path + ".lock", FileMode.OpenOrCreate, FileAccess.ReadWrite,
            FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            var active = RecoverTail(ResolveActive());
            var catalog = PackedCatalog.Open(active);
            _view = new(active, catalog, CompactSearchState.FromCatalog(catalog),
                ImmutableDictionary.Create<string, PackedRecord?>(StringComparer.OrdinalIgnoreCase), catalog.Checkpoint);
            using var input = new FileStream(active, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
            input.Position = catalog.BaseFileLength;
            while (input.Position < input.Length)
            {
                var frame = ReadFrame(input)!;
                var transaction = Decode(frame.Body);
                _view = Apply(_view, transaction.ExpectedSequence, transaction.Previous, transaction.Next, transaction.Mutations, frame.Hash);
            }
            CleanupInactive(active);
        }
        catch { _lease.Dispose(); throw; }
    }

    internal PackedRecord? FindRecord(string path)
    {
        var view = Volatile.Read(ref _view);
        if (view.Delta.TryGetValue(path, out var entry)) return entry;
        var id = view.Catalog.Find(path);
        return id < 0 ? null : view.Catalog.GetRecord(id);
    }

    internal PackedCommitResult Commit(long expectedSequence, PackedSourcePosition? previous, PackedSourcePosition? next,
        IReadOnlyList<PackedMutation> mutations)
    {
        lock (_gate)
        {
            CheckWritable();
            var frame = Encode(expectedSequence, previous, next, mutations);
            var hash = Convert.ToHexString(SHA256.HashData(frame));
            if (expectedSequence == _view.Checkpoint.Sequence - 1 && hash == _view.Checkpoint.LastBatchHash) return new(true, null, false);
            var candidate = Apply(_view, expectedSequence, previous, next, mutations, hash);
            try
            {
                using var output = new FileStream(candidate.FilePath, FileMode.Open, FileAccess.Write, FileShare.ReadWrite | FileShare.Delete);
                output.Position = output.Length;
                output.Write(frame, 0, 12);
                output.Flush(true);
                FaultPoint?.Invoke(PackedWriteStage.JournalHeader);
                output.Write(frame, 12, frame.Length - 12);
                output.Write(Convert.FromHexString(hash));
                output.Flush(true);
                FaultPoint?.Invoke(PackedWriteStage.JournalFlushed);
                Volatile.Write(ref _view, candidate);
            }
            catch { _poisoned = true; throw; }
            try
            {
                if (candidate.Delta.Count >= CheckpointDeltaLimit || new FileInfo(candidate.FilePath).Length - candidate.Catalog.BaseFileLength >= 16 * 1024 * 1024) Checkpoint();
                return new(false, null, false);
            }
            catch (Exception error) { return new(false, error.Message, _poisoned); }
        }
    }

    private View Apply(View view, long expectedSequence, PackedSourcePosition? previous,
        PackedSourcePosition? next, IReadOnlyList<PackedMutation> mutations, string hash)
    {
        if (expectedSequence != view.Checkpoint.Sequence || previous != view.Checkpoint.Source)
            throw new InvalidDataException("Packed işlem sırası veya kaynak konumu uyuşmuyor.");
        if (previous is not null && (next is null || previous.VolumeId != next.VolumeId ||
            previous.JournalId != next.JournalId || previous.RootGeneration != next.RootGeneration || next.NextUsn < previous.NextUsn))
            throw new InvalidDataException("Packed kaynak zinciri kesildi.");
        if (next is { NextUsn: < 0 }) throw new InvalidDataException("Packed USN geçersiz.");
        var changes = view.Delta.ToBuilder();
        using var metadataValidator = new BinaryWriter(Stream.Null, Encoding.UTF8, true);
        var removed = new List<string>(); var upserts = new List<SearchItem>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var mutation in mutations)
        {
            if (string.IsNullOrEmpty(mutation.Path) || !seen.Add(mutation.Path) ||
                mutation.Record is { } value && !string.Equals(value.Item.FullPath, mutation.Path, StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Packed işlem yolu geçersiz veya yinelenmiş.");
            changes[mutation.Path] = mutation.Record;
            if (mutation.Record is { } record)
            {
                if (record.IndexedUtc <= 0) throw new InvalidDataException("Packed güncelleme indeks tarihi eksik.");
                PackedFormat.WriteMetadata(metadataValidator, record, false, record.IndexedUtc);
                upserts.Add(record.Item);
            }
            else removed.Add(mutation.Path);
        }
        var state = view.State.WithRecordChanges(removed, upserts, _tokenizer);
        return new(view.FilePath, view.Catalog, state, changes.ToImmutable(),
            new(checked(expectedSequence + 1), next, hash, PackedCheckpoint.CurrentContract));
    }

    internal void Checkpoint(CancellationToken ct = default)
    {
        lock (_gate)
        {
            CheckWritable();
            var old = _view;
            var scratch = _path + ".scratch-" + Guid.NewGuid().ToString("N");
            var temp = _path + ".generation-" + Guid.NewGuid().ToString("N");
            var published = false;
            try
            {
                using var builder = new PackedCatalogBuilder(scratch, _tokenizer);
                var directories = new Dictionary<string, (int Id, string Path)>(StringComparer.OrdinalIgnoreCase);
                void Add(PackedRecord record)
                {
                    ct.ThrowIfCancellationRequested();
                    var parent = record.Item.ParentPath is { } p && directories.TryGetValue(p, out var found) ? found : (-1, (string?)null);
                    var id = builder.Add(record, parent.Item1, parent.Item2);
                    if (record.Item.IsDirectory) directories.Add(record.Item.FullPath, (id, record.Item.FullPath));
                }
                foreach (var record in Records(old, true).OrderBy(record => record.Item.FullPath.Length)) Add(record);
                foreach (var record in Records(old, false)) Add(record);
                var catalog = builder.Complete(temp, old.Checkpoint, checked(old.Catalog.Generation + 1), ct);
                FaultPoint?.Invoke(PackedWriteStage.CheckpointFlushed);
                ct.ThrowIfCancellationRequested();
                PublishHead(temp);
                published = true;
                _poisoned = true;
                FaultPoint?.Invoke(PackedWriteStage.CheckpointReplaced);
                Volatile.Write(ref _view, new(temp, catalog, CompactSearchState.FromCatalog(catalog),
                    ImmutableDictionary.Create<string, PackedRecord?>(StringComparer.OrdinalIgnoreCase), old.Checkpoint));
                old.Catalog.RetireFile(old.FilePath);
                _poisoned = false;
            }
            finally
            {
                if (!published) TryDelete(temp);
                if (Directory.Exists(scratch)) Directory.Delete(scratch, false);
            }
        }
    }

    private static IEnumerable<PackedRecord> Records(View view, bool directories)
    {
        var filter = new QueryCatalogFilter(!directories, directories, false, false, null, null, null, null, null, null);
        var cache = new Dictionary<int, string>();
        for (var id = 0; id < view.Catalog.ItemCount; id++)
        {
            if (!view.Catalog.Matches(id, filter)) continue;
            var record = view.Catalog.GetRecord(id, cache, true);
            if (!view.Delta.ContainsKey(record.Item.FullPath)) yield return record;
        }
        foreach (var record in view.Delta.Values)
            if (record is not null && record.Item.IsDirectory == directories) yield return record;
    }

    private void CheckWritable()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_poisoned) throw new InvalidOperationException("Packed yazıcı yeniden açılmalı.");
    }

    private string RecoverTail(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.Read | FileShare.Delete);
        Span<byte> header = stackalloc byte[PackedFormat.HeaderSize]; stream.ReadExactly(header);
        var baseLength = (long)BinaryPrimitives.ReadInt32LittleEndian(header[40..]) + 32;
        if (baseLength < PackedFormat.HeaderSize + 32 || baseLength > stream.Length) throw new InvalidDataException("Packed base eksik.");
        stream.Position = baseLength;
        while (stream.Position < stream.Length)
        {
            var start = stream.Position;
            if (ReadFrame(stream) is not null) continue;
            var recovered = _path + ".generation-" + Guid.NewGuid().ToString("N");
            using (var output = new FileStream(recovered, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            {
                stream.Position = 0;
                var buffer = new byte[65536];
                for (var remaining = start; remaining > 0;)
                { var count = (int)Math.Min(remaining, buffer.Length); stream.ReadExactly(buffer, 0, count); output.Write(buffer, 0, count); remaining -= count; }
                output.Flush(true);
            }
            PublishHead(recovered);
            return recovered;
        }
        return path;
    }

    private string ResolveActive()
    {
        var head = _path + ".head";
        if (!File.Exists(head)) return _path;
        var bytes = File.ReadAllBytes(head);
        if (bytes.Length < 33 || bytes.Length > 4096 || !CryptographicOperations.FixedTimeEquals(SHA256.HashData(bytes.AsSpan(0, bytes.Length - 32)), bytes.AsSpan(bytes.Length - 32)))
            throw new InvalidDataException("Packed etkin nesil checksum uyuşmuyor.");
        var name = Encoding.UTF8.GetString(bytes, 0, bytes.Length - 32);
        var prefix = Path.GetFileName(_path) + ".generation-";
        if (!name.StartsWith(prefix, StringComparison.Ordinal) || !Guid.TryParseExact(name[prefix.Length..], "N", out _))
            throw new InvalidDataException("Packed etkin nesil adı geçersiz.");
        return Path.Combine(Path.GetDirectoryName(_path)!, name);
    }
    private void PublishHead(string active)
    {
        var temp = _path + ".head-next-" + Guid.NewGuid().ToString("N");
        try
        {
            var bytes = Encoding.UTF8.GetBytes(Path.GetFileName(active));
            using (var output = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
            { output.Write(bytes); output.Write(SHA256.HashData(bytes)); output.Flush(true); }
            File.Move(temp, _path + ".head", true);
        }
        finally { TryDelete(temp); }
    }
    private void CleanupInactive(string active)
    {
        var prefix = Path.GetFileName(_path) + ".generation-";
        foreach (var file in Directory.EnumerateFiles(Path.GetDirectoryName(_path)!, prefix + "*"))
            if (file != active && Guid.TryParseExact(Path.GetFileName(file)[prefix.Length..], "N", out _)) TryDelete(file);
        if (_path != active) TryDelete(_path);
    }
    private static void TryDelete(string path)
    { try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { } }

    private sealed record Frame(byte[] Body, string Hash);
    private static Frame? ReadFrame(Stream stream)
    {
        if (stream.Length - stream.Position < 12) return null;
        var header = new byte[12]; stream.ReadExactly(header);
        var length = BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(4));
        if (BinaryPrimitives.ReadInt32LittleEndian(header) != FrameMagic || length < 0 || length > MaxFrameBytes ||
            BinaryPrimitives.ReadInt32LittleEndian(header.AsSpan(8)) != ~length) throw new InvalidDataException("Packed işlem başlığı bozuk.");
        if (stream.Length - stream.Position < (long)length + 32) return null;
        var frame = new byte[length + 12]; header.CopyTo(frame, 0); stream.ReadExactly(frame, 12, length);
        var expected = new byte[32]; stream.ReadExactly(expected);
        var hash = SHA256.HashData(frame);
        if (!CryptographicOperations.FixedTimeEquals(hash, expected)) throw new InvalidDataException("Packed işlem checksum uyuşmuyor.");
        return new(frame[12..], Convert.ToHexString(hash));
    }

    private static byte[] Encode(long expected, PackedSourcePosition? previous, PackedSourcePosition? next, IReadOnlyList<PackedMutation> mutations)
    {
        using var stream = new MemoryStream();
        using var writer = new BinaryWriter(stream, Encoding.UTF8, true);
        writer.Write(FrameMagic); writer.Write(0); writer.Write(0);
        writer.Write(expected); WriteSource(writer, previous); WriteSource(writer, next); writer.Write(mutations.Count);
        foreach (var mutation in mutations)
        {
            WriteText(writer, mutation.Path); writer.Write(mutation.Record is not null);
            if (mutation.Record is not { } record) continue;
            var item = record.Item;
            WriteText(writer, item.Name); WriteText(writer, item.FullPath); WriteText(writer, item.ParentPath);
            writer.Write(item.IsDirectory); writer.Write(item.SizeBytes.HasValue);
            if (item.SizeBytes is long size) writer.Write(size);
            WriteDate(writer, item.CreatedTime); WriteDate(writer, item.LastWriteTime); writer.Write(item.OpenCount);
            writer.Write(record.ModifiedUtc); writer.Write(record.CreatedUtc); writer.Write(record.Hidden);
            writer.Write(record.System); writer.Write(record.IndexedUtc);
        }
        var length = checked((int)stream.Length - 12);
        if (length > MaxFrameBytes) throw new InvalidOperationException("Packed işlem 64 MiB sınırını geçti.");
        stream.Position = 4; writer.Write(length); writer.Write(~length);
        return stream.ToArray();
    }
    private sealed record Transaction(long ExpectedSequence, PackedSourcePosition? Previous, PackedSourcePosition? Next, PackedMutation[] Mutations);
    private static Transaction Decode(byte[] bytes)
    {
        using var stream = new MemoryStream(bytes, false); using var reader = new BinaryReader(stream);
        var expected = reader.ReadInt64(); var previous = ReadSource(reader); var next = ReadSource(reader);
        var count = reader.ReadInt32();
        if (count < 0 || count > bytes.Length / 5) throw new InvalidDataException("Packed işlem sayısı geçersiz.");
        var mutations = new PackedMutation[count];
        for (var i = 0; i < count; i++)
        {
            var path = ReadText(reader) ?? throw new InvalidDataException("Packed işlem yolu eksik.");
            PackedRecord? record = null;
            if (reader.ReadBoolean())
            {
                var name = ReadText(reader)!; var fullPath = ReadText(reader)!; var parent = ReadText(reader);
                var directory = reader.ReadBoolean(); long? size = reader.ReadBoolean() ? reader.ReadInt64() : null;
                var item = new SearchItem(name, fullPath, directory, size, ReadDate(reader), ReadDate(reader), reader.ReadInt32(), parent);
                record = new(item, reader.ReadInt64(), reader.ReadInt64(), reader.ReadBoolean(), reader.ReadBoolean(), reader.ReadInt64());
            }
            mutations[i] = new(path, record);
        }
        if (stream.Position != stream.Length) throw new InvalidDataException("Packed işlem uzunluğu uyuşmuyor.");
        return new(expected, previous, next, mutations);
    }
    private static void WriteSource(BinaryWriter writer, PackedSourcePosition? source)
    {
        writer.Write(source is not null); if (source is null) return;
        WriteText(writer, source.VolumeId); writer.Write(source.JournalId); writer.Write(source.NextUsn); writer.Write(source.RootGeneration.ToByteArray());
    }
    private static PackedSourcePosition? ReadSource(BinaryReader reader) => reader.ReadBoolean()
        ? new(ReadText(reader)!, reader.ReadUInt64(), reader.ReadInt64(), new Guid(reader.ReadBytes(16))) : null;
    private static void WriteText(BinaryWriter writer, string? value)
    { writer.Write(value?.Length ?? -1); if (value is not null) writer.Write(MemoryMarshal.AsBytes(value.AsSpan())); }
    private static string? ReadText(BinaryReader reader)
    {
        var length = reader.ReadInt32(); if (length == -1) return null;
        if (length < 0 || (long)length * 2 > reader.BaseStream.Length - reader.BaseStream.Position) throw new InvalidDataException("Packed işlem metni eksik.");
        return new string(MemoryMarshal.Cast<byte, char>(reader.ReadBytes(checked(length * 2))));
    }
    private static void WriteDate(BinaryWriter writer, DateTime? date)
    { writer.Write(date.HasValue); if (date is { } value) { writer.Write(value.Ticks); writer.Write((byte)value.Kind); } }
    private static DateTime? ReadDate(BinaryReader reader) => reader.ReadBoolean() ? new(reader.ReadInt64(), (DateTimeKind)reader.ReadByte()) : null;
    public void Dispose() { lock (_gate) { if (_disposed) return; _disposed = true; _lease.Dispose(); } }
}
