using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Services;

public sealed class LocalizedRecoveryTests
{
    [Fact]
    public async Task MissingDatabaseRowIsRepairedWithoutScanningSiblingsOrLosingHistory()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var file = workspace.CreateFile(Path.Combine("root", "report.txt"), "old");
        var sibling = workspace.CreateFile(Path.Combine("root", "sibling.txt"), "old");
        var db = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService();
        using var manager = Create(db, watcher);
        await Initialize(manager, root);
        watcher.Stop();
        manager.IncrementOpenCount(file);
        var original = Assert.Single(manager.CurrentSearchState.Get("report"));
        db.DeleteFile(file);
        File.WriteAllText(file, "updated report");
        File.WriteAllText(sibling, "a sibling change that must remain outside this repair");
        Assert.Empty(await manager.ApplyExternalChangesWithRecoveryAsync(
            [new() { ChangeType = FileChangeType.Modified, FullPath = file }], true, default));
        Assert.Equal(new FileInfo(file).Length, db.GetFileByPath(file)!.SizeBytes);
        Assert.Equal(original.OpenCount, db.GetFileByPath(file)!.OpenCount);
        Assert.Equal(original.CreatedTime, Assert.Single(manager.CurrentSearchState.Get("report")).CreatedTime);
        Assert.Equal(3, db.GetFileByPath(sibling)!.SizeBytes);
        Assert.Equal(3, Assert.Single(manager.CurrentSearchState.Get("sibling")).SizeBytes);
        Assert.Equal(0, manager.PendingRepairCount);
    }

    [Fact]
    public async Task PendingScopesSurviveRestartAndMergeBeforeAdoptionWithoutRootScanning()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var file = workspace.CreateFile(Path.Combine("root", "report.txt"), "old");
        var sibling = workspace.CreateFile(Path.Combine("root", "sibling.txt"), "old");
        var path = Path.Combine(workspace.Path, "index.db");
        using (var manager = Create(new IndexDatabase(path), new FileWatcherService()))
            await Initialize(manager, root);
        File.WriteAllText(file, "changed while closed");
        File.WriteAllText(sibling, "unrelated change remains outside recovery scope");
        using (var db = new IndexDatabase(path))
        {
            db.Open();
            db.SetMetadata(IndexManager.PendingRepairsKey, Encode([file]));
        }
        var reopenedDb = new IndexDatabase(path);
        var watcher = new FileWatcherService();
        using var restarted = Create(reopenedDb, watcher);
        var missing = Path.Combine(root, "gone.txt");
        await restarted.InitializeWithWatcherFenceAsync([root], default, (_, _) =>
        {
            Assert.Equal(1, restarted.PendingRepairCount);
            Assert.True(restarted.QueueKnownRepairs([missing]));
            Assert.Equal(2, JsonSerializer.Deserialize<string[]>(reopenedDb.GetMetadata(IndexManager.PendingRepairsKey)!)!.Length);
            restarted.NoteChangeFeedCoverage(true);
            return Task.FromResult(false);
        });
        watcher.Stop();
        await WaitFor(() => restarted.PendingRepairCount == 0);
        Assert.Equal(new FileInfo(file).Length, reopenedDb.GetFileByPath(file)!.SizeBytes);
        Assert.Equal(3, reopenedDb.GetFileByPath(sibling)!.SizeBytes);
        Assert.Empty(JsonSerializer.Deserialize<string[]>(reopenedDb.GetMetadata(IndexManager.PendingRepairsKey)!)!);
    }

    [Fact]
    public async Task WatcherFailureStaysInMemoryWhenDurableQueueCannotBeWritten()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var file = workspace.CreateFile(Path.Combine("root", "report.txt"), "old");
        var path = Path.Combine(workspace.Path, "index.db");
        var db = new IndexDatabase(path);
        var watcher = new FileWatcherService();
        using var manager = Create(db, watcher);
        await Initialize(manager, root);
        watcher.Stop();
        Sql(path, "CREATE TRIGGER fail_file BEFORE INSERT ON Files BEGIN SELECT RAISE(ABORT, 'test file failure'); END; " +
            "CREATE TRIGGER fail_pending BEFORE INSERT ON Metadata WHEN NEW.Key='pending_repair_scopes_utf16' BEGIN SELECT RAISE(ABORT, 'test pending failure'); END;");
        File.WriteAllText(file, "updated report");
        Assert.False(manager.ApplyExternalChanges([new() { ChangeType = FileChangeType.Modified, FullPath = file }]));
        Assert.Equal(1, manager.PendingRepairCount);
        Assert.Null(db.GetMetadata(IndexManager.PendingRepairsKey));
        Sql(path, "DROP TRIGGER fail_file; DROP TRIGGER fail_pending;");
        Assert.True(manager.QueueKnownRepairs([file]));
        await WaitFor(() => manager.PendingRepairCount == 0);
        Assert.Equal(new FileInfo(file).Length, db.GetFileByPath(file)!.SizeBytes);
    }

    [Fact]
    public async Task DatabaseScopeQueriesIncludeDescendantsAndExcludeSiblingPrefixes()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var scope = workspace.CreateDirectory(Path.Combine("root", "local"));
        var nested = workspace.CreateDirectory(Path.Combine("root", "local", "nested"));
        var first = workspace.CreateFile(Path.Combine("root", "local", "one.txt"));
        var second = workspace.CreateFile(Path.Combine("root", "local", "nested", "two.txt"));
        workspace.CreateDirectory(Path.Combine("root", "localSibling"));
        workspace.CreateFile(Path.Combine("root", "localSibling", "outside.txt"));
        var db = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService();
        using var manager = Create(db, watcher);
        await Initialize(manager, root);
        watcher.Stop();
        Assert.Equal(new[] { scope, nested }, db.GetDirectoriesInScope(scope).Select(row => row.FullPath));
        Assert.Equal(new[] { first, second }.Order(), db.GetFilesInScope(scope).Select(row => row.FullPath).Order());
        Assert.Equal(first, Assert.Single(db.GetFilesInScope(first)).FullPath);
        Assert.Empty(db.GetDirectoriesInScope(first));
    }

    private static IndexManager Create(IndexDatabase db, FileWatcherService watcher) =>
        new(db, watcher, reconciliationInterval: TimeSpan.FromSeconds(30), layout: SearchStateLayout.Compact);

    private static Task Initialize(IndexManager manager, string root) =>
        manager.InitializeWithWatcherFenceAsync([root], default, (_, _) =>
        {
            manager.NoteChangeFeedCoverage(true);
            return Task.FromResult(false);
        });

    private static string Encode(IEnumerable<string> paths) => JsonSerializer.Serialize(paths.Select(path =>
        Convert.ToBase64String(MemoryMarshal.AsBytes(path.AsSpan()))).ToArray());

    private static void Sql(string path, string sql)
    {
        using var connection = new SqliteConnection($"Data Source={path};Pooling=False");
        connection.Open();
        using var command = connection.CreateCommand();
        command.CommandText = sql;
        command.ExecuteNonQuery();
    }

    private static async Task WaitFor(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }
}
