using System.Collections;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexDatabaseBulkWriteTests
{
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(63)]
    [InlineData(64)]
    [InlineData(65)]
    [InlineData(128)]
    [InlineData(129)]
    public void FullGroupsTailAndReuseMatchSingleRowWrites(int count)
    {
        using var workspace = new TemporaryDirectory();
        using var expected = OpenDatabase(workspace, "expected.db");
        using var actual = OpenDatabase(workspace, "actual.db");
        var files = CreateFiles(count);
        using (var expectedTransaction = expected.BeginTransaction())
        using (var actualTransaction = actual.BeginTransaction())
        using (var writer = actual.CreatePreparedWriteBatch())
        {
            foreach (var file in files) expected.InsertFile(file);
            writer.InsertFiles(files);
            AssertFilesEqual(expected, actual);
            foreach (var file in files)
            {
                file.Extension = "";
                file.DirectoryId = 2;
                file.SizeBytes = 0;
                file.CreatedTimeUtc = 900;
                file.LastWriteTimeUtc = 800;
                file.LastIndexedTimeUtc = 700;
                file.OpenCount = 99;
                file.IsHidden = false;
                file.IsSystem = false;
                expected.InsertFile(file);
            }
            writer.InsertFiles(files);
            expectedTransaction.Commit();
            actualTransaction.Commit();
        }
        actual.Close();
        actual.Open();
        AssertFilesEqual(expected, actual);
    }

    [Fact]
    public void DuplicatePathsWithinAndAcrossGroupsKeepIdentityAndHistory()
    {
        using var workspace = new TemporaryDirectory();
        using var expected = OpenDatabase(workspace, "expected.db");
        using var actual = OpenDatabase(workspace, "actual.db");
        var files = CreateFiles(129);
        files[31].FullPath = files[0].FullPath;
        files[64].FullPath = files[0].FullPath;
        files[128].FullPath = files[0].FullPath;
        using (var expectedTransaction = expected.BeginTransaction())
        using (var actualTransaction = actual.BeginTransaction())
        using (var writer = actual.CreatePreparedWriteBatch())
        {
            foreach (var file in files) expected.InsertFile(file);
            writer.InsertFiles(files);
            expectedTransaction.Commit();
            actualTransaction.Commit();
        }
        AssertFilesEqual(expected, actual);
        var updated = actual.GetFileByPath(files[0].FullPath)!;
        Assert.Equal(1, updated.Id);
        Assert.Equal(files[0].CreatedTimeUtc, updated.CreatedTimeUtc);
        Assert.Equal(files[0].OpenCount, updated.OpenCount);
        Assert.Equal(files[128].FileName, updated.FileName);
    }

    [Theory]
    [InlineData(31)]
    [InlineData(78)]
    [InlineData(128)]
    public void ForeignKeyFailureRollsBackEarlierGroupsClearTokensAndMetadata(int invalidIndex)
    {
        using var workspace = new TemporaryDirectory();
        using var database = OpenDatabase(workspace, "index.db");
        var old = CreateFiles(1)[0];
        var oldId = database.InsertFile(old);
        database.LinkFileToToken(oldId, database.GetOrCreateToken("kept"));
        database.SetMetadata("bulk-test", "before");
        using (var transaction = database.BeginTransaction())
        {
            database.ClearIndex();
            database.SetMetadata("bulk-test", "during");
            using var writer = database.CreatePreparedWriteBatch();
            var rootId = writer.InsertDirectory(new IndexedDirectory { FullPath = @"C:\new", Name = "new" });
            var files = CreateFiles(129);
            foreach (var file in files) file.DirectoryId = rootId;
            files[invalidIndex].DirectoryId = long.MaxValue;
            var error = Assert.Throws<SqliteException>(() => writer.InsertFiles(files));
            Assert.Equal(19, error.SqliteErrorCode);
            transaction.Rollback();
        }
        Assert.Equal(oldId, Assert.Single(database.GetAllFiles()).Id);
        Assert.Equal(new[] { oldId }, database.GetFileIdsByToken("kept"));
        Assert.Equal("before", database.GetMetadata("bulk-test"));
        Assert.Null(database.GetDirectoryByPath(@"C:\new"));
        using var connection = new SqliteConnection($"Data Source={database.DatabasePath};Mode=ReadOnly;Pooling=False");
        connection.Open();
        using var check = connection.CreateCommand();
        check.CommandText = "PRAGMA foreign_key_check;";
        Assert.Null(check.ExecuteScalar());
        check.CommandText = "PRAGMA integrity_check;";
        Assert.Equal("ok", check.ExecuteScalar());
    }

    [Fact]
    public void CancellationDuringSecondGroupDoesNotFlushItOnDispose()
    {
        using var workspace = new TemporaryDirectory();
        using var database = OpenDatabase(workspace, "index.db");
        using var cancellation = new CancellationTokenSource();
        using var transaction = database.BeginTransaction();
        var writer = database.CreatePreparedWriteBatch();
        var files = new CancelOnRead(CreateFiles(129), 64, cancellation);
        Assert.Throws<OperationCanceledException>(() => writer.InsertFiles(files, cancellation.Token));
        Assert.Equal(64, database.GetFileCount());
        writer.Dispose();
        Assert.Equal(64, database.GetFileCount());
        Assert.Throws<ObjectDisposedException>(() => writer.InsertFiles(Array.Empty<IndexedFile>()));
        transaction.Rollback();
        Assert.Equal(0, database.GetFileCount());
    }

    private static IndexDatabase OpenDatabase(TemporaryDirectory workspace, string name)
    {
        var database = new IndexDatabase(Path.Combine(workspace.Path, name));
        database.Open();
        database.InsertDirectory(new IndexedDirectory { FullPath = @"C:\root", Name = "root" });
        database.InsertDirectory(new IndexedDirectory { FullPath = @"D:\root", Name = "root" });
        return database;
    }

    private static IndexedFile[] CreateFiles(int count) => Enumerable.Range(0, count).Select(index =>
        new IndexedFile
        {
            FullPath = @"C:\root\İş ' 📄-" + index + ".txt",
            FileName = "İş ' 📄-" + index + ".txt", Extension = ".txt", DirectoryId = index % 2 + 1,
            SizeBytes = 5_000_000_000L + index, CreatedTimeUtc = 100 + index,
            LastWriteTimeUtc = 200 + index, LastIndexedTimeUtc = 300 + index,
            OpenCount = index + 7, IsHidden = index % 2 == 0, IsSystem = index % 3 == 0
        }).ToArray();

    private static void AssertFilesEqual(IndexDatabase expected, IndexDatabase actual) =>
        Assert.Equal(JsonSerializer.Serialize(expected.GetAllFiles()), JsonSerializer.Serialize(actual.GetAllFiles()));

    private sealed class CancelOnRead(IndexedFile[] files, int cancelAt, CancellationTokenSource cancellation)
        : IReadOnlyList<IndexedFile>
    {
        public int Count => files.Length;
        public IndexedFile this[int index]
        {
            get
            {
                if (index == cancelAt) cancellation.Cancel();
                return files[index];
            }
        }
        public IEnumerator<IndexedFile> GetEnumerator() => ((IEnumerable<IndexedFile>)files).GetEnumerator();
        IEnumerator IEnumerable.GetEnumerator() => GetEnumerator();
    }
}
