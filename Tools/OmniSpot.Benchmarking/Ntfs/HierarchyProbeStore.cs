using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;

namespace OmniSpot.Benchmarking.Ntfs;

internal sealed class HierarchyProbeStore : IDisposable
{
    private readonly SqliteConnection _connection;
    private readonly SqliteTransaction _transaction;
    private readonly SqliteCommand _insert;
    private readonly long _indexedUtc = DateTime.UtcNow.Ticks;

    internal HierarchyProbeStore(string path, string root)
    {
        using (new FileStream(path, FileMode.CreateNew, FileAccess.Write)) { }
        _connection = new(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        _connection.Open();
        Execute("PRAGMA journal_mode=WAL; PRAGMA synchronous=NORMAL; PRAGMA foreign_keys=ON;");
        Execute("""
            CREATE TABLE Entries(Id INTEGER PRIMARY KEY, ParentId INTEGER REFERENCES Entries(Id) ON DELETE CASCADE,
                Name TEXT NOT NULL, IsDirectory INTEGER NOT NULL, SizeBytes INTEGER NOT NULL,
                CreatedTimeUtc INTEGER NOT NULL, LastWriteTimeUtc INTEGER NOT NULL, LastIndexedTimeUtc INTEGER NOT NULL,
                OpenCount INTEGER NOT NULL, IsHidden INTEGER NOT NULL, IsSystem INTEGER NOT NULL,
                UNIQUE(ParentId,Name));
            CREATE TABLE Metadata(Key TEXT PRIMARY KEY, Value TEXT NOT NULL);
            """);
        _transaction = _connection.BeginTransaction();
        using var metadata = _connection.CreateCommand();
        metadata.Transaction = _transaction;
        metadata.CommandText = "INSERT INTO Metadata VALUES('Root', $root),('PrototypeVersion','1');";
        metadata.Parameters.AddWithValue("$root", root);
        metadata.ExecuteNonQuery();
        _insert = _connection.CreateCommand();
        _insert.Transaction = _transaction;
        _insert.CommandText = "INSERT INTO Entries VALUES($id,$parent,$name,$directory,$size,$created,$modified,$indexed,$open,$hidden,$system);";
        foreach (var name in new[] { "id", "parent", "name", "directory", "size", "created", "modified", "indexed", "open", "hidden", "system" })
            _insert.Parameters.AddWithValue("$" + name, 0);
        _insert.Prepare();
    }

    internal void Add(IndexManager.HierarchyProbeEntry entry, int id, int parent)
    {
        var values = new object[] { id + 1, parent < 0 ? DBNull.Value : parent + 1, entry.Item.Name,
            entry.Item.IsDirectory ? 1 : 0, entry.Item.SizeBytes ?? 0, entry.CreatedUtc, entry.ModifiedUtc,
            _indexedUtc, entry.Item.OpenCount, entry.Hidden ? 1 : 0, entry.System ? 1 : 0 };
        for (var i = 0; i < values.Length; i++) _insert.Parameters[i].Value = values[i];
        _insert.ExecuteNonQuery();
    }

    internal void Complete()
    {
        using var index = _connection.CreateCommand();
        index.Transaction = _transaction;
        index.CommandText = "CREATE INDEX idx_entries_name ON Entries(Name); INSERT INTO Metadata VALUES('Complete','1');";
        index.ExecuteNonQuery();
        _transaction.Commit();
    }

    private void Execute(string sql)
    {
        using var command = _connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    internal static IEnumerable<IndexManager.HierarchyProbeEntry> Read(string path, bool normalized = false, bool filesFirst = false)
    {
        using var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        connection.Open();
        if (!normalized)
        {
            foreach (var table in filesFirst ? new[] { "Files", "Directories" } : new[] { "Directories", "Files" })
            {
                using var command = connection.CreateCommand();
                command.CommandText = table == "Directories"
                    ? "SELECT FullPath,Name,0,0,LastWriteTimeUtc,0,IsHidden,0,ParentId FROM Directories ORDER BY Depth,Id;"
                    : "SELECT FullPath,FileName,SizeBytes,CreatedTimeUtc,LastWriteTimeUtc,OpenCount,IsHidden,IsSystem,DirectoryId FROM Files ORDER BY Id;";
                if (filesFirst && table == "Directories") command.CommandText = command.CommandText.Replace("ORDER BY Depth,Id", "ORDER BY Depth DESC,Id DESC", StringComparison.Ordinal);
                using var reader = command.ExecuteReader();
                while (reader.Read())
                {
                    var directory = table == "Directories";
                    var fullPath = reader.GetString(0);
                    var parent = directory && reader.IsDBNull(8) ? "" : Path.GetDirectoryName(fullPath);
                    yield return Entry(fullPath, reader.GetString(1), parent, directory,
                        reader.GetInt64(2), reader.GetInt64(3), reader.GetInt64(4), reader.GetInt32(5),
                        reader.GetBoolean(6), reader.GetBoolean(7));
                }
            }
        }
        else
        {
            using var rootCommand = connection.CreateCommand();
            rootCommand.CommandText = "SELECT Value FROM Metadata WHERE Key='Root';";
            var root = (string)rootCommand.ExecuteScalar()!;
            var directories = new Dictionary<long, string>();
            using var command = connection.CreateCommand();
            command.CommandText = "SELECT Id,ParentId,Name,IsDirectory,SizeBytes,CreatedTimeUtc,LastWriteTimeUtc,OpenCount,IsHidden,IsSystem FROM Entries ORDER BY Id;";
            using var reader = command.ExecuteReader();
            while (reader.Read())
            {
                var parent = reader.IsDBNull(1) ? "" : directories[reader.GetInt64(1)];
                var pathValue = parent.Length == 0 ? root : Path.Combine(parent, reader.GetString(2));
                var directory = reader.GetBoolean(3);
                if (directory) directories.Add(reader.GetInt64(0), pathValue);
                yield return Entry(pathValue, reader.GetString(2), parent, directory, reader.GetInt64(4),
                    reader.GetInt64(5), reader.GetInt64(6), reader.GetInt32(7), reader.GetBoolean(8), reader.GetBoolean(9));
            }
        }
    }

    private static IndexManager.HierarchyProbeEntry Entry(string path, string name, string? parent, bool directory,
        long size, long created, long modified, int open, bool hidden, bool system) =>
        new(new SearchItem(name, path, directory, directory ? null : size,
            directory ? null : new DateTime(created, DateTimeKind.Utc).ToLocalTime(),
            directory ? null : new DateTime(modified, DateTimeKind.Utc).ToLocalTime(), open, parent), modified, created, hidden, system);

    public void Dispose() { _insert.Dispose(); _transaction.Dispose(); _connection.Dispose(); }
}
