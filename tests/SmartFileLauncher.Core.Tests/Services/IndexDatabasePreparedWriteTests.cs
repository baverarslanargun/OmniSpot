using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexDatabasePreparedWriteTests
{
    [Fact]
    public void ReusedCommandsResetValuesAndPreserveUpsertIdentityAndHistory()
    {
        using var workspace = new TemporaryDirectory();
        using var database = OpenDatabase(workspace);
        var firstRoot = new IndexedDirectory { FullPath = @"C:\İş ' 📁", Name = "İş ' 📁" };
        var secondRoot = new IndexedDirectory { FullPath = @"C:\ikinci", Name = "ikinci" };
        var child = new IndexedDirectory {
            FullPath = firstRoot.FullPath + @"\alt", Name = "alt", Depth = 1,
            IsHidden = true, LastWriteTimeUtc = 123, LastIndexedTimeUtc = 456
        };
        long firstRootId, secondRootId, childId, fileId;
        using (var transaction = database.BeginTransaction())
        {
            using (var writer = database.CreatePreparedWriteBatch())
            {
                firstRootId = writer.InsertDirectory(firstRoot);
                child.ParentId = firstRootId;
                childId = writer.InsertDirectory(child);
                secondRootId = writer.InsertDirectory(secondRoot);
                var file = new IndexedFile {
                    FullPath = child.FullPath + @"\İ'📄.txt", FileName = "İ'📄.txt", Extension = ".txt",
                    DirectoryId = childId, SizeBytes = 5_000_000_000L,
                    CreatedTimeUtc = 100, LastWriteTimeUtc = 200, LastIndexedTimeUtc = 300,
                    OpenCount = 7, IsHidden = true, IsSystem = true
                };
                writer.InsertFile(file);
                fileId = database.GetFileByPath(file.FullPath)!.Id;
                Assert.Equal(5_000_000_000L, database.GetFileByPath(file.FullPath)!.SizeBytes);
                writer.InsertFile(new IndexedFile {
                    FullPath = secondRoot.FullPath + @"\plain", FileName = "plain", Extension = "",
                    DirectoryId = secondRootId, SizeBytes = 0, CreatedTimeUtc = 400,
                    LastWriteTimeUtc = 500, LastIndexedTimeUtc = 600
                });
                file.FileName = "updated.txt";
                file.Extension = ".new";
                file.DirectoryId = secondRootId;
                file.SizeBytes = 42;
                file.CreatedTimeUtc = 900;
                file.OpenCount = 99;
                file.LastWriteTimeUtc = 700;
                file.LastIndexedTimeUtc = 800;
                file.IsHidden = false;
                file.IsSystem = false;
                writer.InsertFile(file);
                Assert.Equal(fileId, database.GetFileByPath(file.FullPath)!.Id);
                child.ParentId = secondRootId;
                child.Name = "updated";
                child.Depth = 2;
                child.LastWriteTimeUtc = 789;
                child.LastIndexedTimeUtc = 987;
                child.IsHidden = false;
                Assert.Equal(childId, writer.InsertDirectory(child));
            }
            transaction.Commit();
        }
        database.Close();
        database.Open();
        var root = database.GetDirectoryByPath(secondRoot.FullPath)!;
        Assert.Null(root.ParentId);
        Assert.Equal(0, root.Depth);
        Assert.False(root.IsHidden);
        var updatedChild = database.GetDirectoryByPath(child.FullPath)!;
        Assert.Equal(secondRootId, updatedChild.ParentId);
        Assert.Equal("updated", updatedChild.Name);
        Assert.Equal(2, updatedChild.Depth);
        Assert.Equal(789, updatedChild.LastWriteTimeUtc);
        Assert.Equal(987, updatedChild.LastIndexedTimeUtc);
        Assert.False(updatedChild.IsHidden);
        var updated = database.GetFileByPath(child.FullPath + @"\İ'📄.txt")!;
        Assert.Equal(fileId, updated.Id);
        Assert.Equal("updated.txt", updated.FileName);
        Assert.Equal(".new", updated.Extension);
        Assert.Equal(secondRootId, updated.DirectoryId);
        Assert.Equal(42, updated.SizeBytes);
        Assert.Equal(100, updated.CreatedTimeUtc);
        Assert.Equal(7, updated.OpenCount);
        Assert.Equal(700, updated.LastWriteTimeUtc);
        Assert.Equal(800, updated.LastIndexedTimeUtc);
        Assert.False(updated.IsHidden);
        Assert.False(updated.IsSystem);
        var plain = database.GetFileByPath(secondRoot.FullPath + @"\plain")!;
        Assert.Equal("", plain.Extension);
        Assert.Equal(0, plain.SizeBytes);
        Assert.Equal(400, plain.CreatedTimeUtc);
        Assert.Equal(500, plain.LastWriteTimeUtc);
        Assert.Equal(600, plain.LastIndexedTimeUtc);
        Assert.Equal(0, plain.OpenCount);
        Assert.False(plain.IsHidden);
        Assert.False(plain.IsSystem);
    }

    [Fact]
    public void FailedBatchRollsBackClearWritesTokensAndMetadataTogether()
    {
        using var workspace = new TemporaryDirectory();
        using var database = OpenDatabase(workspace);
        var oldRoot = database.InsertDirectory(new IndexedDirectory { FullPath = @"C:\old", Name = "old" });
        var oldFile = database.InsertFile(new IndexedFile {
            FullPath = @"C:\old\kept.txt", FileName = "kept.txt", Extension = ".txt", DirectoryId = oldRoot
        });
        database.LinkFileToToken(oldFile, database.GetOrCreateToken("kept"));
        database.SetMetadata("batch-test", "before");

        using (var transaction = database.BeginTransaction())
        {
            database.ClearIndex();
            database.SetMetadata("batch-test", "during");
            using (var writer = database.CreatePreparedWriteBatch())
            {
                var root = writer.InsertDirectory(new IndexedDirectory { FullPath = @"C:\new", Name = "new" });
                writer.InsertFile(new IndexedFile { FullPath = @"C:\new\valid", FileName = "valid", DirectoryId = root });
                var failure = Assert.Throws<SqliteException>(() => writer.InsertFile(new IndexedFile {
                    FullPath = @"C:\new\invalid", FileName = "invalid", DirectoryId = long.MaxValue
                }));
                Assert.Equal(19, failure.SqliteErrorCode);
            }
            transaction.Rollback();
        }

        Assert.Equal(oldFile, database.GetFileByPath(@"C:\old\kept.txt")!.Id);
        Assert.Equal(new[] { oldFile }, database.GetFileIdsByToken("kept"));
        Assert.Equal("before", database.GetMetadata("batch-test"));
        Assert.Null(database.GetDirectoryByPath(@"C:\new"));
        Assert.Null(database.GetFileByPath(@"C:\new\valid"));
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(check.ExecuteScalar());
        check.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", check.ExecuteScalar());
    }

    [Fact]
    public void DisposingBatchLeavesCallerTransactionUsableAndRejectsFurtherWrites()
    {
        using var workspace = new TemporaryDirectory();
        using var database = OpenDatabase(workspace);
        using var transaction = database.BeginTransaction();
        var writer = database.CreatePreparedWriteBatch();
        var root = new IndexedDirectory { FullPath = @"C:\root", Name = "root" };
        var rootId = writer.InsertDirectory(root);
        writer.Dispose();
        writer.Dispose();
        var file = new IndexedFile { FullPath = @"C:\root\file", FileName = "file", DirectoryId = rootId };
        Assert.Throws<ObjectDisposedException>(() => writer.InsertDirectory(root));
        Assert.Throws<ObjectDisposedException>(() => writer.InsertFile(file));
        database.InsertFile(file);
        transaction.Commit();
        Assert.NotNull(database.GetFileByPath(file.FullPath));
    }

    private static IndexDatabase OpenDatabase(TemporaryDirectory workspace)
    {
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        database.Open();
        return database;
    }
}
