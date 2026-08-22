using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexManagerPathInvariantTests
{
    [Fact]
    public async Task BootstrapScanStoresCanonicalKeysMatchingNodeFullPath()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateDirectory(Path.Combine("root", "nested", "deeper"));
        workspace.CreateFile(Path.Combine("root", "top.txt"));
        workspace.CreateFile(Path.Combine("root", "nested", "middle.txt"));
        workspace.CreateFile(Path.Combine("root", "nested", "deeper", "leaf.txt"));

        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            Assert.NotEmpty(manager.IndexedEntries);
            AssertPathInvariant(manager);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task CacheLoadStoresCanonicalKeysMatchingNodeFullPath()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateDirectory(Path.Combine("root", "nested"));
        workspace.CreateFile(Path.Combine("root", "top.txt"));
        workspace.CreateFile(Path.Combine("root", "nested", "leaf.txt"));

        var databasePath = Path.Combine(workspace.Path, "index.db");

        var seedDatabase = new IndexDatabase(databasePath);
        var seedWatcher = new FileWatcherService(debounceMs: 1);
        var seedManager = new IndexManager(seedDatabase, seedWatcher);
        int seededCount;
        try
        {
            await seedManager.InitializeAsync(root);
            seedWatcher.Stop();
            seededCount = seedManager.IndexedEntries.Count;
        }
        finally
        {
            seedManager.Dispose();
        }

        var database = new IndexDatabase(databasePath);
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);
        var statuses = new List<string>();
        manager.OnProgress += progress => statuses.Add(progress.Status);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            Assert.Contains(statuses, status => status.Contains("Önbellekten"));
            Assert.DoesNotContain(statuses, status => status.Contains("İlk kurulum"));
            Assert.Equal(seededCount, manager.IndexedEntries.Count);
            AssertPathInvariant(manager);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task IncrementalAddStoresCanonicalKeyWhenEventPathIsNotCanonical()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            var addedDirectory = workspace.CreateDirectory(Path.Combine("root", "added"));
            var addedFile = workspace.CreateFile(Path.Combine("root", "added", "new.txt"));

            manager.ApplyFileChange(new FileChangeEvent
            {
                ChangeType = FileChangeType.Created,
                FullPath = addedDirectory.Replace(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                IsDirectory = true
            });

            Assert.NotNull(manager.GetNode(addedDirectory));
            Assert.NotNull(manager.GetNode(addedFile));
            AssertPathInvariant(manager);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task NonCanonicalCachedFileRejectsCacheAndRebuilds()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "real.txt"));

        var databasePath = Path.Combine(workspace.Path, "index.db");
        var nonCanonicalPath = Path.Combine(root, "stale") +
                               Path.AltDirectorySeparatorChar + "ghost.txt";

        await SeedIndexAsync(databasePath, root);

        var tampered = new IndexDatabase(databasePath);
        tampered.Open();
        var rootDirectory = tampered.GetDirectoryByPath(root);
        Assert.NotNull(rootDirectory);
        tampered.InsertFile(new IndexedFile
        {
            FullPath = nonCanonicalPath,
            FileName = "ghost.txt",
            Extension = ".txt",
            DirectoryId = rootDirectory!.Id
        });
        Assert.NotNull(tampered.GetFileByPath(nonCanonicalPath));
        tampered.Dispose();

        var database = new IndexDatabase(databasePath);
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);
        var statuses = new List<string>();
        manager.OnProgress += progress => statuses.Add(progress.Status);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            Assert.Contains(statuses, status => status.Contains("İlk kurulum"));
            Assert.Null(manager.GetNode(nonCanonicalPath));
            Assert.Null(database.GetFileByPath(nonCanonicalPath));
            AssertPathInvariant(manager);
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task NonCanonicalCachedDirectoryRejectsCacheAndRebuilds()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "real.txt"));

        var databasePath = Path.Combine(workspace.Path, "index.db");
        var nonCanonicalPath = Path.Combine(root, "stale") + Path.DirectorySeparatorChar;

        await SeedIndexAsync(databasePath, root);

        var tampered = new IndexDatabase(databasePath);
        tampered.Open();
        tampered.InsertDirectory(new IndexedDirectory
        {
            FullPath = nonCanonicalPath,
            Name = "stale",
            ParentId = null,
            Depth = 1
        });
        Assert.NotNull(tampered.GetDirectoryByPath(nonCanonicalPath));
        tampered.Dispose();

        var database = new IndexDatabase(databasePath);
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);
        var statuses = new List<string>();
        manager.OnProgress += progress => statuses.Add(progress.Status);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            Assert.Contains(statuses, status => status.Contains("İlk kurulum"));
            Assert.Null(database.GetDirectoryByPath(nonCanonicalPath));
            AssertPathInvariant(manager);
        }
        finally
        {
            manager.Dispose();
        }
    }

    private static async Task SeedIndexAsync(string databasePath, string root)
    {
        var database = new IndexDatabase(databasePath);
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);
        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();
        }
        finally
        {
            manager.Dispose();
        }
    }

    private static void AssertPathInvariant(IndexManager manager)
    {
        foreach (var entry in manager.IndexedEntries)
        {
            Assert.Equal(entry.Key, entry.Value.FullPath, StringComparer.Ordinal);
            Assert.True(
                IndexManager.IsCanonicalIndexedPath(entry.Key),
                $"kanonik olmayan anahtar: {entry.Key}");
        }
    }
}
