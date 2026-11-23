using System.Diagnostics;
using Microsoft.Data.Sqlite;

namespace SmartFileLauncher.Core.Search;

public sealed class LocalOpenHistory : IDisposable
{
    internal const int MaxPaths = 2048;
    private const int WriteBatchSize = 128;
    private const int SqliteBusy = 5;
    private const int SqliteLocked = 6;
    private readonly object _gate = new();
    private readonly Dictionary<string, OpenUsage> _usage = new(StringComparer.OrdinalIgnoreCase);
    private readonly Queue<OpenUsage> _pending = new();
    private readonly string _databasePath;
    private readonly Action<string>? _logStorageFailure;
    private LiteralRanking? _snapshot = LiteralRanking.Empty;
    private DateOnly _snapshotDate;
    private Task? _worker;
    private Task? _shutdown;
    private bool _loadPending = true;
    private bool _reportedStorageFailure;

    public LocalOpenHistory(string databasePath, Action<string>? logStorageFailure = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(databasePath);
        _databasePath = databasePath;
        _logStorageFailure = logStorageFailure;
        lock (_gate)
            StartPendingWorker();
    }

    public LiteralRanking Snapshot
    {
        get
        {
            lock (_gate)
            {
                var now = DateTime.UtcNow;
                var today = DateOnly.FromDateTime(now);
                if (_snapshot is null || (_usage.Count != 0 && _snapshotDate != today))
                {
                    _snapshot = new LiteralRanking(_usage.Values.Select(usage => (usage.FullPath, Boost(usage, now.Ticks))));
                    _snapshotDate = today;
                }
                return _snapshot;
            }
        }
    }

    public void RecordOpened(string fullPath)
    {
        if (string.IsNullOrWhiteSpace(fullPath)) return;
        lock (_gate)
        {
            if (_shutdown is not null) return;
            var usage = new OpenUsage(fullPath, 1, DateTime.UtcNow.Ticks);
            AddUsage(usage);
            PruneUsage();
            _snapshot = null;
            _pending.Enqueue(usage);
            StartPendingWorker();
        }
    }

    public Task FlushAsync()
    {
        lock (_gate)
            return _shutdown ?? StartPendingWorker() ?? Task.CompletedTask;
    }

    public void Dispose()
    {
        Task shutdown;
        lock (_gate)
            shutdown = _shutdown ??= FinishPendingWritesAsync(_worker);
        shutdown.GetAwaiter().GetResult();
    }

    private Task? StartPendingWorker()
    {
        if (_worker is null && (_loadPending || _pending.Count > 0))
            _worker = Task.Run(WritePending);
        return _worker;
    }

    private async Task FinishPendingWritesAsync(Task? worker)
    {
        if (worker is not null)
            await worker.ConfigureAwait(false);
        lock (_gate)
            worker = _pending.Count > 0 ? StartPendingWorker() : null;
        if (worker is not null)
            await worker.ConfigureAwait(false);
    }

    private void AddUsage(OpenUsage usage)
    {
        if (_usage.TryGetValue(usage.FullPath, out var previous))
        {
            var newest = usage.LastOpenedUtc >= previous.LastOpenedUtc ? usage : previous;
            usage = newest with { Count = (int)Math.Min(int.MaxValue, (long)previous.Count + usage.Count) };
        }
        _usage[usage.FullPath] = usage;
    }

    private void PruneUsage()
    {
        while (_usage.Count > MaxPaths)
        {
            var oldest = _usage.Values.MinBy(usage => usage.LastOpenedUtc);
            _usage.Remove(oldest.FullPath);
        }
    }

    private void LoadSaved()
    {
        if (!File.Exists(_databasePath)) return;
        using var connection = OpenDatabase();
        using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT path, open_count, last_opened FROM opened_paths
            ORDER BY last_opened DESC, rowid DESC LIMIT $limit
            """;
        command.Parameters.AddWithValue("$limit", MaxPaths);
        using var reader = command.ExecuteReader();
        var saved = new List<OpenUsage>();
        while (reader.Read())
            saved.Add(new OpenUsage(reader.GetString(0), reader.GetInt32(1), reader.GetInt64(2)));
        lock (_gate)
        {
            foreach (var usage in saved)
                AddUsage(usage);
            PruneUsage();
            _snapshot = null;
        }
    }

    private void WritePending()
    {
        if (_loadPending)
        {
            var loaded = TryStorage(LoadSaved);
            lock (_gate)
            {
                if (loaded == StorageResult.Busy)
                {
                    _worker = null;
                    return;
                }
                _loadPending = false;
            }
        }

        var batch = new List<OpenUsage>(WriteBatchSize);
        while (true)
        {
            lock (_gate)
            {
                if (_pending.Count == 0)
                {
                    _worker = null;
                    return;
                }
                batch.AddRange(_pending.Take(WriteBatchSize));
            }

            var written = TryStorage(() => Persist(batch));
            lock (_gate)
            {
                if (written == StorageResult.Busy)
                {
                    _worker = null;
                    return;
                }
                for (var i = 0; i < batch.Count; i++)
                    _pending.Dequeue();
            }
            batch.Clear();
        }
    }

    private void Persist(List<OpenUsage> batch)
    {
        if (Path.GetDirectoryName(_databasePath) is { Length: > 0 } directory)
            Directory.CreateDirectory(directory);
        using var connection = OpenDatabase();
        using var transaction = connection.BeginTransaction();
        using var upsert = connection.CreateCommand();
        upsert.Transaction = transaction;
        upsert.CommandText = """
            INSERT INTO opened_paths (path, open_count, last_opened)
            VALUES ($path, $count, $lastOpened)
            ON CONFLICT(path) DO UPDATE SET
                path = CASE WHEN excluded.last_opened >= last_opened THEN excluded.path ELSE path END,
                open_count = min(open_count + excluded.open_count, 2147483647),
                last_opened = max(last_opened, excluded.last_opened)
            """;
        var path = upsert.Parameters.Add("$path", SqliteType.Text);
        var count = upsert.Parameters.Add("$count", SqliteType.Integer);
        var lastOpened = upsert.Parameters.Add("$lastOpened", SqliteType.Integer);
        foreach (var usage in batch)
        {
            path.Value = usage.FullPath;
            count.Value = usage.Count;
            lastOpened.Value = usage.LastOpenedUtc;
            upsert.ExecuteNonQuery();
        }

        using var prune = connection.CreateCommand();
        prune.Transaction = transaction;
        prune.CommandText = """
            DELETE FROM opened_paths WHERE rowid IN (
                SELECT rowid FROM opened_paths ORDER BY last_opened DESC, rowid DESC LIMIT -1 OFFSET $limit
            )
            """;
        prune.Parameters.AddWithValue("$limit", MaxPaths);
        prune.ExecuteNonQuery();
        transaction.Commit();
    }

    private SqliteConnection OpenDatabase()
    {
        var builder = new SqliteConnectionStringBuilder
        {
            DataSource = _databasePath,
            Mode = SqliteOpenMode.ReadWriteCreate,
            DefaultTimeout = 1,
            Pooling = false
        };
        var connection = new SqliteConnection(builder.ToString());
        try
        {
            connection.Open();
            connection.CreateCollation("ORDINALIGNORECASE", StringComparer.OrdinalIgnoreCase.Compare);
            using var command = connection.CreateCommand();
            command.CommandText = """
                CREATE TABLE IF NOT EXISTS opened_paths (
                    path TEXT PRIMARY KEY COLLATE ORDINALIGNORECASE NOT NULL,
                    open_count INTEGER NOT NULL CHECK (open_count BETWEEN 1 AND 2147483647),
                    last_opened INTEGER NOT NULL
                )
                """;
            command.ExecuteNonQuery();
            return connection;
        }
        catch
        {
            connection.Dispose();
            throw;
        }
    }

    private StorageResult TryStorage(Action action)
    {
        try
        {
            action();
            _reportedStorageFailure = false;
            return StorageResult.Succeeded;
        }
        catch (SqliteException failure) when (failure.SqliteErrorCode is SqliteBusy or SqliteLocked)
        {
            ReportStorageFailure("OmniSpot yerel açılış geçmişi SQLite kilidi nedeniyle bekliyor.");
            return StorageResult.Busy;
        }
        catch (Exception failure) when (failure is SqliteException or IOException or UnauthorizedAccessException
            or ArgumentException or InvalidCastException or FormatException or OverflowException)
        {
            ReportStorageFailure("OmniSpot yerel açılış geçmişi depolamasına erişemedi.");
            return StorageResult.Unavailable;
        }
    }

    private void ReportStorageFailure(string message)
    {
        if (_reportedStorageFailure) return;
        _reportedStorageFailure = true;
        if (_logStorageFailure is null)
        {
            Trace.TraceWarning(message);
            return;
        }

        try
        {
            _logStorageFailure(message);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException or InvalidOperationException)
        {
            Trace.TraceWarning(message);
        }
    }

    private static double Boost(OpenUsage usage, long now)
    {
        var ageDays = Math.Max(0d, (now - (double)usage.LastOpenedUtc) / TimeSpan.TicksPerDay);
        var frequency = 0.3 * usage.Count / (usage.Count + 4d);
        var recency = 0.2 / (1d + ageDays / 14d);
        return frequency + recency;
    }

    private enum StorageResult
    {
        Succeeded,
        Busy,
        Unavailable
    }

    private readonly record struct OpenUsage(string FullPath, int Count, long LastOpenedUtc);
}
