using System.Runtime.InteropServices;
using System.Text.Json;
using SmartFileLauncher.Core.Models;

namespace SmartFileLauncher.Core.Services;

public partial class IndexManager
{
    internal const string PendingRepairsKey = "pending_repair_scopes_utf16";
    private const int MaximumPendingRepairs = 4096;
    private const int MaximumPendingRepairBytes = 4 * 1024 * 1024;
    private readonly Dictionary<string, long> _pendingRepairs = new(StringComparer.OrdinalIgnoreCase);
    private long _repairVersion;
    private int _fullReconciliationRequested;
    private bool _knownRecoveryOnly;

    internal int PendingRepairCount { get { lock (_lock) return _pendingRepairs.Count; } }

    internal bool QueueKnownRepairs(IReadOnlyList<string> paths)
    {
        string? failure = null;
        lock (_lock)
        {
            try
            {
                ObjectDisposedException.ThrowIf(_disposed, this);
                var normalized = paths.Select(ValidateRepairPath).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (normalized.Length == 0) return false;
                var combined = _pendingRepairs.Keys.Concat(normalized).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
                if (combined.Length > MaximumPendingRepairs) return false;
                var json = EncodePendingRepairs(combined);
                using var transaction = _db.BeginTransaction();
                _db.SetMetadata(PendingRepairsKey, json);
                transaction.Commit();
                foreach (var path in normalized) _pendingRepairs[path] = ++_repairVersion;
            }
            catch (Exception error) { failure = error.Message; }
        }
        if (failure is not null)
        {
            NotifyError($"Yerel onarımlar kaydedilemedi: {failure}");
            return false;
        }
        SignalReconciliation();
        return true;
    }

    internal void NoteKnownRecovery(IReadOnlyList<string> paths)
    {
        _knownRecoveryOnly = true;
        RememberKnownRepairs(paths);
    }

    private void RememberKnownRepairs(IReadOnlyList<string> paths)
    {
        lock (_lock)
        {
            foreach (var path in paths)
            {
                try
                {
                    var normalized = ValidateRepairPath(path);
                    _pendingRepairs[normalized] = ++_repairVersion;
                }
                catch (Exception error) when (error is ArgumentException or NotSupportedException or PathTooLongException) { }
            }
        }
        SignalReconciliation();
    }

    private string ValidateRepairPath(string path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path))
            throw new ArgumentException("Yerel onarım yolu geçersiz.");
        var normalized = NormalizeIndexedPath(path);
        if (!_activeRootPaths.Any(root => IsSameOrDescendantPath(normalized, root)))
            throw new ArgumentException("Yerel onarım indeks kapsamının dışında.");
        return normalized;
    }

    private static string EncodePendingRepairs(IEnumerable<string> paths)
    {
        var json = JsonSerializer.Serialize(paths.Select(path =>
            Convert.ToBase64String(MemoryMarshal.AsBytes(path.AsSpan()))).ToArray());
        if (json.Length > MaximumPendingRepairBytes)
            throw new InvalidDataException("Yerel onarım kuyruğu kapasitesine ulaştı.");
        return json;
    }

    private void LoadPendingRepairs()
    {
        lock (_lock)
        {
            try
            {
                var json = _db.GetMetadata(PendingRepairsKey);
                if (string.IsNullOrWhiteSpace(json)) return;
                if (json.Length > MaximumPendingRepairBytes) throw new InvalidDataException("Yerel onarım kaydı çok büyük.");
                var encoded = JsonSerializer.Deserialize<string[]>(json) ?? throw new InvalidDataException("Yerel onarım kaydı geçersiz.");
                if (encoded.Length > MaximumPendingRepairs) throw new InvalidDataException("Yerel onarım kaydı çok büyük.");
                foreach (var value in encoded)
                {
                    var bytes = Convert.FromBase64String(value);
                    if (bytes.Length == 0 || bytes.Length % 2 != 0) throw new InvalidDataException("Yerel onarım yolu geçersiz.");
                    var path = new string(MemoryMarshal.Cast<byte, char>(bytes));
                    if (!Path.IsPathFullyQualified(path)) throw new InvalidDataException("Yerel onarım yolu geçersiz.");
                    path = NormalizeIndexedPath(path);
                    if (_activeRootPaths.Any(root => IsSameOrDescendantPath(path, root)) && !_pendingRepairs.ContainsKey(path))
                        _pendingRepairs[path] = ++_repairVersion;
                }
            }
            catch (Exception error)
            {
                Interlocked.Exchange(ref _fullReconciliationRequested, 1);
                _knownRecoveryOnly = false;
                NotifyError($"Yerel onarım kaydı okunamadı: {error.Message}");
            }
        }
    }

    private async Task RepairPendingScopesAsync(CancellationToken ct)
    {
        KeyValuePair<string, long>[] pending;
        lock (_lock) pending = _pendingRepairs.ToArray();
        foreach (var entry in pending)
        {
            ct.ThrowIfCancellationRequested();
            if (!await ReconcileWithinLifecycleAsync(entry.Key, ct).ConfigureAwait(false)) continue;
            lock (_lock)
            {
                if (!_pendingRepairs.TryGetValue(entry.Key, out var version) || version != entry.Value) continue;
                using var transaction = _db.BeginTransaction();
                _db.SetMetadata(PendingRepairsKey, EncodePendingRepairs(_pendingRepairs.Keys.Where(path =>
                    !string.Equals(path, entry.Key, StringComparison.OrdinalIgnoreCase))));
                transaction.Commit();
                _pendingRepairs.Remove(entry.Key);
            }
        }
    }

    internal async Task<IReadOnlyList<string>> ApplyExternalChangesWithRecoveryAsync(IReadOnlyList<FileChangeEvent> changes,
        bool withinLifecycle, CancellationToken ct)
    {
        if (ApplyExternalChangesCore(changes, out var failed, deferFailures: false)) return [];
        if (failed.Count == 0) failed.AddRange(changes);
        var remaining = new List<string>();
        foreach (var path in failed.SelectMany(change => change.OldPath is null
                     ? new[] { change.FullPath } : new[] { change.FullPath, change.OldPath })
                     .Distinct(StringComparer.OrdinalIgnoreCase))
        {
            ct.ThrowIfCancellationRequested();
            try
            {
                var normalized = ValidateRepairPath(path);
                var recovered = withinLifecycle
                    ? await ReconcileWithinLifecycleAsync(normalized, ct).ConfigureAwait(false)
                    : await EnsureSyncedAsync(normalized, ct).ConfigureAwait(false);
                if (!recovered) remaining.Add(normalized);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception error)
            {
                NotifyError($"Yerel onarım başarısız: {path}: {error.Message}");
                remaining.Add(path);
            }
        }
        return remaining;
    }
}
