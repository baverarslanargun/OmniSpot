using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexDatabaseTests
{
    [Fact]
    public void DeleteDirectory_CascadesAfterConnectionReopen()
    {
        using var workspace = new TemporaryDirectory();
        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        database.Open();

        var now = DateTime.UtcNow.Ticks;
        var rootPath = Path.Combine(workspace.Path, "root");
        var childPath = Path.Combine(rootPath, "child");
        var filePath = Path.Combine(childPath, "file.txt");

        var rootId = database.InsertDirectory(new IndexedDirectory
        {
            FullPath = rootPath,
            Name = "root",
            Depth = 0,
            LastWriteTimeUtc = now,
            LastIndexedTimeUtc = now
        });
        var childId = database.InsertDirectory(new IndexedDirectory
        {
            FullPath = childPath,
            Name = "child",
            ParentId = rootId,
            Depth = 1,
            LastWriteTimeUtc = now,
            LastIndexedTimeUtc = now
        });
        var fileId = database.InsertFile(new IndexedFile
        {
            FullPath = filePath,
            FileName = "file.txt",
            Extension = ".txt",
            DirectoryId = childId,
            SizeBytes = 4,
            CreatedTimeUtc = now,
            LastWriteTimeUtc = now,
            LastIndexedTimeUtc = now
        });
        var tokenId = database.GetOrCreateToken("file");
        database.LinkFileToToken(fileId, tokenId);

        database.Close();
        database.Open();
        database.DeleteDirectory(rootPath);

        Assert.Empty(database.GetAllDirectories());
        Assert.Null(database.GetFileByPath(filePath));
        Assert.Empty(database.GetFileIdsByToken("file"));
    }

    [Fact]
    public void Open_RepairsLegacyOrphanRows()
    {
        using var workspace = new TemporaryDirectory();
        var databasePath = Path.Combine(workspace.Path, "index.db");
        using var database = new IndexDatabase(databasePath);
        database.Open();
        database.Close();

        var orphanDirectoryPath = Path.Combine(workspace.Path, "orphan-directory");
        var orphanFilePath = Path.Combine(workspace.Path, "orphan-file.txt");
        using (var legacyConnection = new SqliteConnection(
                   new SqliteConnectionStringBuilder
                   {
                       DataSource = databasePath,
                       Mode = SqliteOpenMode.ReadWrite,
                       Pooling = false
                   }.ToString()))
        {
            legacyConnection.Open();
            using var command = legacyConnection.CreateCommand();
            command.CommandText = @"
                PRAGMA foreign_keys=OFF;
                INSERT INTO Tokens (Id, Token) VALUES (500, 'legacy-orphan-token');
                INSERT INTO Directories
                    (Id, FullPath, Name, ParentId, Depth, LastWriteTimeUtc, LastIndexedTimeUtc)
                VALUES
                    (500, @directoryPath, 'orphan-directory', 9999, 1, 1, 1);
                INSERT INTO Files
                    (Id, FullPath, FileName, Extension, DirectoryId, SizeBytes,
                     CreatedTimeUtc, LastWriteTimeUtc, LastIndexedTimeUtc)
                VALUES
                    (500, @filePath, 'orphan-file.txt', '.txt', 9998, 1, 1, 1, 1);
                INSERT INTO FileTokens (FileId, TokenId) VALUES (9997, 500);
            ";
            command.Parameters.AddWithValue("@directoryPath", orphanDirectoryPath);
            command.Parameters.AddWithValue("@filePath", orphanFilePath);
            command.ExecuteNonQuery();
        }

        database.Open();

        Assert.Null(database.GetDirectoryByPath(orphanDirectoryPath));
        Assert.Null(database.GetFileByPath(orphanFilePath));
        Assert.Empty(database.GetFileIdsByToken("legacy-orphan-token"));
    }
}
