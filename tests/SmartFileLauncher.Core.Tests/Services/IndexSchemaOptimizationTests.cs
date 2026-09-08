using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexSchemaOptimizationTests
{
    private static readonly string[] RedundantIndexes =
    {
        "idx_directories_path",
        "idx_files_path",
        "idx_tokens_token"
    };

    [Fact]
    public void Open_PrunesLegacyRedundantIndexesWithoutChangingPopulatedVersionOneDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var databasePath = Path.Combine(workspace.Path, "index.db");
        var directoryPath = Path.Combine(workspace.Path, "root");
        var filePath = Path.Combine(directoryPath, "preserved.txt");
        var now = DateTime.UtcNow.Ticks;

        using (var database = new IndexDatabase(databasePath))
        {
            database.Open();
            var directoryId = database.InsertDirectory(new IndexedDirectory
            {
                FullPath = directoryPath,
                Name = "root",
                Depth = 0,
                LastWriteTimeUtc = now,
                LastIndexedTimeUtc = now
            });
            var fileId = database.InsertFile(new IndexedFile
            {
                FullPath = filePath,
                FileName = "preserved.txt",
                Extension = ".txt",
                DirectoryId = directoryId,
                SizeBytes = 42,
                CreatedTimeUtc = now,
                LastWriteTimeUtc = now,
                LastIndexedTimeUtc = now
            });
            var tokenId = database.GetOrCreateToken("preserved");
            database.LinkFileToToken(fileId, tokenId);
            database.SetMetadata("optimization_test", "preserve-me");
        }

        using (var legacyConnection = OpenConnection(databasePath))
        {
            ExecuteNonQuery(legacyConnection, @"
                CREATE INDEX IF NOT EXISTS idx_directories_path ON Directories(FullPath);
                CREATE INDEX IF NOT EXISTS idx_files_path ON Files(FullPath);
                CREATE INDEX IF NOT EXISTS idx_tokens_token ON Tokens(Token);
            ");
            Assert.All(RedundantIndexes, name => Assert.True(IndexExists(legacyConnection, name)));
        }

        using (var database = new IndexDatabase(databasePath))
        {
            database.Open();
            database.Close();
            database.Open();

            Assert.Equal("1", database.GetMetadata(IndexMetadata.Keys.SchemaVersion));
            Assert.Equal("preserve-me", database.GetMetadata("optimization_test"));
            Assert.Equal(directoryPath, database.GetDirectoryByPath(directoryPath)?.FullPath);
            Assert.Equal(filePath, database.GetFileByPath(filePath)?.FullPath);
            Assert.Single(database.GetFileIdsByToken("preserved"));
        }

        using var connection = OpenConnection(databasePath);
        Assert.All(RedundantIndexes, name => Assert.False(IndexExists(connection, name)));
        AssertUsesUniqueAutoIndex(connection, "SELECT * FROM Directories WHERE FullPath = @value", directoryPath);
        AssertUsesUniqueAutoIndex(connection, "SELECT * FROM Files WHERE FullPath = @value", filePath);
        AssertUsesUniqueAutoIndex(connection, "SELECT * FROM Tokens WHERE Token = @value", "preserved");

        AssertUniqueConstraint(connection,
            "INSERT INTO Directories (FullPath, Name, Depth, LastWriteTimeUtc, LastIndexedTimeUtc) VALUES (@value, 'duplicate', 0, 1, 1)",
            directoryPath);
        AssertUniqueConstraint(connection,
            "INSERT INTO Files (FullPath, FileName, Extension, DirectoryId, SizeBytes, CreatedTimeUtc, LastWriteTimeUtc, LastIndexedTimeUtc) SELECT @value, 'duplicate.txt', '.txt', Id, 1, 1, 1, 1 FROM Directories LIMIT 1",
            filePath);
        AssertUniqueConstraint(connection,
            "INSERT INTO Tokens (Token) VALUES (@value)",
            "preserved");
    }

    [Fact]
    public void FreshSchema_OmitsRedundantIndexesAndSeedValidationDoesNotMutateVersionOneDatabase()
    {
        using var workspace = new TemporaryDirectory();
        var databasePath = Path.Combine(workspace.Path, "index.db");
        using (var database = new IndexDatabase(databasePath))
        {
            database.Open();
        }

        var beforeBytes = File.ReadAllBytes(databasePath);
        var beforeWriteTime = File.GetLastWriteTimeUtc(databasePath);

        IndexDatabase.ValidateSeed(databasePath);

        Assert.Equal(beforeBytes, File.ReadAllBytes(databasePath));
        Assert.Equal(beforeWriteTime, File.GetLastWriteTimeUtc(databasePath));
        Assert.False(File.Exists(databasePath + "-wal"));
        Assert.False(File.Exists(databasePath + "-shm"));

        using var connection = OpenConnection(databasePath);
        Assert.Equal("1", ExecuteScalar(connection,
            "SELECT Value FROM Metadata WHERE Key = 'schema_version'"));
        Assert.All(RedundantIndexes, name => Assert.False(IndexExists(connection, name)));
        Assert.True(IndexExists(connection, "sqlite_autoindex_Directories_1"));
        Assert.True(IndexExists(connection, "sqlite_autoindex_Files_1"));
        Assert.True(IndexExists(connection, "sqlite_autoindex_Tokens_1"));
    }

    private static SqliteConnection OpenConnection(string databasePath)
    {
        var connection = new SqliteConnection(new SqliteConnectionStringBuilder
        {
            DataSource = databasePath,
            Mode = SqliteOpenMode.ReadWrite,
            Pooling = false
        }.ToString());
        connection.Open();
        return connection;
    }

    private static bool IndexExists(SqliteConnection connection, string name)
    {
        using var command = connection.CreateCommand();
        command.CommandText =
            "SELECT COUNT(*) FROM sqlite_master WHERE type = 'index' AND name = @name";
        command.Parameters.AddWithValue("@name", name);
        return Convert.ToInt64(command.ExecuteScalar()) == 1;
    }

    private static void AssertUsesUniqueAutoIndex(
        SqliteConnection connection,
        string sql,
        string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = "EXPLAIN QUERY PLAN " + sql;
        command.Parameters.AddWithValue("@value", value);
        using var reader = command.ExecuteReader();
        Assert.True(reader.Read());
        Assert.Contains("sqlite_autoindex_", reader.GetString(3), StringComparison.Ordinal);
    }

    private static void AssertUniqueConstraint(
        SqliteConnection connection,
        string sql,
        string value)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.Parameters.AddWithValue("@value", value);
        var exception = Assert.Throws<SqliteException>(() => command.ExecuteNonQuery());
        Assert.Equal(19, exception.SqliteErrorCode);
    }

    private static void ExecuteNonQuery(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static string? ExecuteScalar(SqliteConnection connection, string sql)
    {
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        return command.ExecuteScalar()?.ToString();
    }
}
