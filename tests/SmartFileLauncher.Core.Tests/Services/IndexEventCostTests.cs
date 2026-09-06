using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexEventCostTests
{
    [Theory]
    [InlineData(200)]
    [InlineData(2000)]
    public async Task OneFileEvent_TouchesOneNodeWhateverTheIndexSize(int fileCount)
    {
        using var world = await World.WithFiles(fileCount);

        var target = world.FilePath(0);
        var before = world.Manager.SubtreeNodesInspected;

        world.Manager.ApplyFileChange(new FileChangeEvent
        {
            ChangeType = FileChangeType.Modified,
            FullPath = target,
            IsDirectory = false
        });

        Assert.Equal(1, world.Manager.SubtreeNodesInspected - before);
    }

    [Fact]
    public async Task DeletingOneFile_TouchesOneNodeNotTheWholeIndex()
    {
        using var world = await World.WithFiles(2000);

        var target = world.FilePath(7);
        File.Delete(target);
        var before = world.Manager.SubtreeNodesInspected;

        world.Manager.ApplyFileChange(new FileChangeEvent
        {
            ChangeType = FileChangeType.Deleted,
            FullPath = target,
            IsDirectory = false
        });

        Assert.Equal(1, world.Manager.SubtreeNodesInspected - before);
        Assert.Null(world.Manager.GetNode(target));
    }

    [Fact]
    public async Task DeletingADirectory_TouchesItsSubtreeNotTheWholeIndex()
    {
        using var world = await World.WithFiles(2000, extraTree: true);

        var branch = Path.Combine(world.Root, "dal");
        Directory.Delete(branch, recursive: true);
        var before = world.Manager.SubtreeNodesInspected;

        world.Manager.ApplyFileChange(new FileChangeEvent
        {
            ChangeType = FileChangeType.Deleted,
            FullPath = branch,
            IsDirectory = true
        });

        var touched = world.Manager.SubtreeNodesInspected - before;

        Assert.Equal(11, touched);
        Assert.Null(world.Manager.GetNode(branch));
        Assert.Null(world.Manager.GetNode(Path.Combine(branch, "dal-0.txt")));
    }

    [Fact]
    public async Task AHealthyCacheReload_LeavesNoDetachedNode()
    {
        using var world = await World.WithFiles(300, extraTree: true);
        world.Manager.Dispose();

        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(new IndexDatabase(world.DatabasePath), watcher);

        try
        {
            await manager.InitializeAsync(new[] { world.Root });

            Assert.Equal(0, manager.DetachedNodeCount);
            Assert.NotNull(manager.GetNode(world.FilePath(0)));
        }
        finally
        {
            manager.Dispose();
            watcher.Dispose();
        }
    }

    [Fact]
    public async Task TheWalkAndTheScan_SeeTheSameSubtree()
    {
        using var world = await World.WithFiles(400, extraTree: true);

        var probes = new[]
        {
            world.Root,
            Path.Combine(world.Root, "dal"),
            Path.Combine(world.Root, "dal", "dal-3.txt"),
            world.FilePath(0),
            Path.Combine(world.Root, "hic-olmayan"),
        };

        foreach (var probe in probes)
        {
            var walked = world.Manager.CollectSubtreeByWalk(probe)
                .Select(node => node.FullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();
            var scanned = world.Manager.CollectSubtreeByScan(probe)
                .Select(node => node.FullPath)
                .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
                .ToArray();

            Assert.Equal(scanned, walked);
        }
    }

    [Fact]
    public async Task DeletingADirectoryRow_TakesItsFilesWithIt()
    {
        using var world = await World.WithFiles(20, extraTree: true);
        var branch = Path.Combine(world.Root, "dal");
        var branchFile = Path.Combine(branch, "dal-0.txt");
        world.Manager.Dispose();

        var database = new IndexDatabase(world.DatabasePath);
        database.Open();

        Assert.NotNull(database.GetFileByPath(branchFile));

        database.DeleteDirectory(branch);

        Assert.Null(database.GetFileByPath(branchFile));
        Assert.Null(database.GetDirectoryByPath(branch));

        database.Close();
    }

    [Fact]
    public async Task ACacheReload_UsesTheCacheAndLeavesNoDetachedNode()
    {
        using var world = await World.WithFiles(50, extraTree: true);
        var branch = Path.Combine(world.Root, "dal");
        world.Manager.Dispose();

        var database = new IndexDatabase(world.DatabasePath);
        database.Open();
        database.DeleteDirectory(branch);
        database.Close();

        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);
        manager.NoteChangeFeedCoverage(true);

        try
        {
            await manager.InitializeAsync(new[] { world.Root });

            Assert.True(
                manager.GetNode(branch) is null,
                "Disktekini değil önbellektekini yüklemeli; aksi hâlde bu testin " +
                "ölçtüğü şey önbellek yolu değildir.");
            Assert.NotNull(manager.GetNode(world.FilePath(0)));
            Assert.Equal(0, manager.DetachedNodeCount);
        }
        finally
        {
            manager.Dispose();
            watcher.Dispose();
        }
    }

    private sealed class World : IDisposable
    {
        private readonly TemporaryDirectory _workspace = new();
        private readonly FileWatcherService _watcher = new(debounceMs: 1);

        private World()
        {
            Root = _workspace.CreateDirectory("kok");
            DatabasePath = Path.Combine(_workspace.Path, "index.db");
            Manager = new IndexManager(new IndexDatabase(DatabasePath), _watcher);
        }

        public IndexManager Manager { get; }

        public string Root { get; }

        public string DatabasePath { get; }

        public static async Task<World> WithFiles(int fileCount, bool extraTree = false)
        {
            var world = new World();

            for (var i = 0; i < fileCount; i++)
            {
                File.WriteAllText(world.FilePath(i), $"veri {i}");
            }

            if (extraTree)
            {
                var branch = Path.Combine(world.Root, "dal");
                Directory.CreateDirectory(branch);
                for (var i = 0; i < 10; i++)
                {
                    File.WriteAllText(Path.Combine(branch, $"dal-{i}.txt"), "dal");
                }
            }

            await world.Manager.InitializeAsync(new[] { world.Root });
            world._watcher.Stop();
            return world;
        }

        public string FilePath(int index) => Path.Combine(Root, $"dosya-{index}.txt");

        public void Dispose()
        {
            Manager.Dispose();
            _watcher.Dispose();
            _workspace.Dispose();
        }
    }
}
