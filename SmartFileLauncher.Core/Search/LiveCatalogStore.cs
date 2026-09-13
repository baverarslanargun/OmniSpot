using System.Security.Cryptography;
using System.Text.Json;

namespace SmartFileLauncher.Core.Search;

internal enum LiveCommitStage { PagesFlushed, JournalFlushed, HeadReplaced }

internal sealed class LiveCatalogStore : IDisposable
{
    private readonly object _gate = new();
    private readonly string _directory;
    private readonly FileStream _writerLease;
    private LiveCatalog _catalog;
    private LiveCatalog.Frame _frame;
    private CompactSearchState _state;
    private bool _poisoned, _disposed;
    internal Action<LiveCommitStage>? FaultPoint { get; set; }
    internal CompactSearchState State => Volatile.Read(ref _state);
    internal int DirectoryCount => Volatile.Read(ref _frame).DirectoryCount;
    internal long Sequence { get { lock (_gate) return _frame.Sequence; } }
    internal string RootPath => _frame.Root;
    internal string DirectoryPath => _directory;
    internal string? DeliveryId { get { lock (_gate) return _frame.DeliveryId; } }
    internal string[] PendingRepairs => Volatile.Read(ref _frame).PendingRepairs ?? [];
    internal static bool Exists(string directory) => File.Exists(Path.Combine(directory, "head.bin")) || File.Exists(Path.Combine(directory, "transaction.wal"));

    internal LiveCatalogStore(string directory, LiveCatalog? initial = null)
    {
        _directory = Path.GetFullPath(directory);
        Directory.CreateDirectory(_directory);
        _writerLease = new(Path.Combine(_directory, "writer.lock"), FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None, 1, FileOptions.DeleteOnClose);
        try
        {
            Recover(_directory);
            if (initial is not null)
            {
                if (File.Exists(Path.Combine(_directory, "head.bin"))) throw new InvalidOperationException("Live katalog zaten var.");
                _catalog = initial; _frame = initial.FinishFrame(0, null); WriteHead(_directory, _frame);
            }
            else { _frame = ReadHead(_directory); _catalog = LiveCatalog.OpenFrame(_directory, _frame); }
            _state = _catalog.CreateSearchState();
            RemoveOrphanPages(_directory, _frame);
        }
        catch { _writerLease.Dispose(); throw; }
    }
    internal PackedRecord? FindRecord(string path)
    { lock (_gate) { ObjectDisposedException.ThrowIf(_disposed, this); return _catalog.FindCurrentRecord(path); } }
    internal bool Commit(IReadOnlyList<LiveMutation> mutations, string? deliveryId = null, CancellationToken ct = default)
        => Commit(candidate => { foreach (var mutation in mutations) { ct.ThrowIfCancellationRequested(); candidate.ApplyMutation(mutation); } }, deliveryId, ct);

    internal bool Commit(Action<LiveCatalog> apply, string? deliveryId = null, CancellationToken ct = default,
        Func<string[]>? pendingRepairs = null, string[]? removeRepairs = null)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_poisoned) throw new InvalidOperationException("Live işlem günlüğü yeniden açılmalı.");
            if (deliveryId is not null && deliveryId == _frame.DeliveryId) return false;
            ct.ThrowIfCancellationRequested();
            var candidate = _catalog.ForkForUpdate(); var committedJournal = false; var published = false;
            try
            {
                apply(candidate);
                var frame = candidate.FinishFrame(checked(_frame.Sequence + 1), deliveryId ?? _frame.DeliveryId);
                frame = frame with { PendingRepairs = PendingRepairs.Except(removeRepairs ?? []).Concat(pendingRepairs?.Invoke() ?? []).Distinct(StringComparer.Ordinal).ToArray() };
                FaultPoint?.Invoke(LiveCommitStage.PagesFlushed); ct.ThrowIfCancellationRequested();
                WriteAtomic(Path.Combine(_directory, "transaction.wal"), frame);
                committedJournal = true; FaultPoint?.Invoke(LiveCommitStage.JournalFlushed);
                WriteHead(_directory, frame); FaultPoint?.Invoke(LiveCommitStage.HeadReplaced);
                var state = candidate.CreateSearchState(); var previous = _catalog;
                _catalog = candidate; _frame = frame; Volatile.Write(ref _state, state); published = true;
                previous.RetireReplacedBy(candidate); previous.Dispose();
                File.Delete(Path.Combine(_directory, "transaction.wal"));
                return true;
            }
            catch
            {
                if (committedJournal) _poisoned = true;
                throw;
            }
            finally
            {
                if (!published) { if (committedJournal) candidate.Dispose(); else candidate.AbandonUpdate(); }
            }
        }
    }
    internal static LiveCatalog.Frame ReadHead(string directory) => ReadFrame(Path.Combine(directory, "head.bin"));
    private static LiveCatalog.Frame ReadFrame(string path)
    {
        var info = new FileInfo(path);
        if (info.Length < 32 || info.Length > 64 * 1024 * 1024) throw new InvalidDataException("Live işlem başlığı boyutu geçersiz.");
        var bytes = File.ReadAllBytes(path); var body = bytes.AsSpan(0, bytes.Length - 32);
        if (!CryptographicOperations.FixedTimeEquals(SHA256.HashData(body), bytes.AsSpan(bytes.Length - 32))) throw new InvalidDataException("Live işlem checksum uyuşmuyor.");
        return JsonSerializer.Deserialize<LiveCatalog.Frame>(body) ?? throw new InvalidDataException("Live işlem başlığı yok.");
    }
    private static void WriteAtomic(string path, LiveCatalog.Frame frame)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame); var temporary = path + ".next";
        using (var output = new FileStream(temporary, FileMode.Create, FileAccess.Write, FileShare.None))
        { output.Write(bytes); output.Write(SHA256.HashData(bytes)); output.Flush(true); }
        File.Move(temporary, path, true);
    }
    private static void WriteHead(string directory, LiveCatalog.Frame frame) => WriteAtomic(Path.Combine(directory, "head.bin"), frame);
    private static void Recover(string directory)
    {
        var journal = Path.Combine(directory, "transaction.wal");
        if (!File.Exists(journal)) return;
        var frame = ReadFrame(journal);
        using var validation = LiveCatalog.OpenFrame(directory, frame);
        var headPath = Path.Combine(directory, "head.bin");
        if (File.Exists(headPath))
        {
            var head = ReadHead(directory);
            if (frame.Root != head.Root || frame.IndexedUtc != head.IndexedUtc || frame.Contract != head.Contract ||
                frame.Sequence < head.Sequence || frame.Sequence > head.Sequence + 1 ||
                frame.Sequence == head.Sequence && !JsonSerializer.SerializeToUtf8Bytes(frame).AsSpan().SequenceEqual(JsonSerializer.SerializeToUtf8Bytes(head)))
                throw new InvalidDataException("Live işlem sırası kopuk.");
        }
        WriteHead(directory, frame); File.Delete(journal);
    }
    private static void RemoveOrphanPages(string directory, LiveCatalog.Frame frame)
    {
        var kept = frame.Areas.SelectMany(area => area.Pages).Select(page => page.File).ToHashSet(StringComparer.OrdinalIgnoreCase);
        foreach (var file in Directory.EnumerateFiles(directory, "*.page", SearchOption.TopDirectoryOnly))
        {
            var name = Path.GetFileName(file);
            if (kept.Contains(name) || !frame.Areas.Any(area => name.StartsWith(area.Name + "-", StringComparison.Ordinal))) continue;
            try { File.Delete(file); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true; _catalog.Dispose(); _writerLease.Dispose();
        }
    }
}
