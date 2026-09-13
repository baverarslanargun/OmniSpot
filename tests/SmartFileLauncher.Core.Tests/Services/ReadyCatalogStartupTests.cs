using System.Collections.Concurrent;
using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Services;

public sealed class ReadyCatalogStartupTests
{
    [Fact]
    public async Task CleanShutdownReloadsReadyCatalogWithOpenCount()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var file = workspace.CreateFile(Path.Combine("root", "report.txt"), "content");
        var path = Path.Combine(workspace.Path, "index.db");
        SearchItem[] expected;
        using (var manager = Create(path))
        {
            await Initialize(manager, root);
            manager.IncrementOpenCount(file);
            expected = manager.CreateSearchState().GetAllItems().OrderBy(item => item.FullPath).ToArray();
        }
        Assert.True(File.Exists(path + ".catalog.meta"));
        using var restarted = Create(path);
        var ready = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        restarted.OnProgress += progress => { if (progress.Phase == "cache_catalog") ready.TrySetResult(); };
        await Initialize(restarted, root);
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(expected, restarted.CreateSearchState().GetAllItems().OrderBy(item => item.FullPath));
    }

    [Fact]
    public async Task CommittedWalChangeUsesSqliteInsteadOfStaleReadyCatalog()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "report.txt"), "content");
        var path = Path.Combine(workspace.Path, "index.db");
        using (var manager = Create(path)) await Initialize(manager, root);
        var originalHash = System.Security.Cryptography.SHA256.HashData(File.ReadAllBytes(path));
        using var writer = new SqliteConnection($"Data Source={path};Pooling=False");
        writer.Open();
        using (var command = writer.CreateCommand())
        {
            command.CommandText = "PRAGMA wal_autocheckpoint=0; UPDATE Files SET OpenCount=41;";
            command.ExecuteNonQuery();
        }
        using (var databaseFile = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite))
            Assert.Equal(originalHash, System.Security.Cryptography.SHA256.HashData(databaseFile));
        Assert.True(new FileInfo(path + "-wal").Length > 0);
        using var restarted = Create(path);
        var phases = new ConcurrentQueue<string?>();
        restarted.OnProgress += progress => phases.Enqueue(progress.Phase);
        await Initialize(restarted, root);
        Assert.Equal(41, Assert.Single(restarted.CreateSearchState().Get("report")).OpenCount);
        Assert.DoesNotContain("cache_catalog", phases);
    }

    private static IndexManager Create(string path) =>
        new(new IndexDatabase(path), new FileWatcherService(), layout: SearchStateLayout.Compact);

    private static Task Initialize(IndexManager manager, string root) =>
        manager.InitializeWithWatcherFenceAsync(new[] { root }, default, (_, _) =>
        {
            manager.NoteChangeFeedCoverage(true);
            return Task.FromResult(false);
        });
}
