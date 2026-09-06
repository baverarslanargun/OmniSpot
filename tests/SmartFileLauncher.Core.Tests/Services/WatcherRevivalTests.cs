using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class WatcherRevivalTests
{
    [Fact]
    public async Task AFaultedWatcher_ComesBack()
    {
        using var world = await World.Ready();

        Assert.True(world.Manager.IsWatching);

        world.Watcher.SimulateWatcherError(new IOException("Test: tampon taştı."));

        Assert.False(world.Manager.IsWatching);

        await world.WaitForRevival();

        Assert.True(
            world.Manager.IsWatching,
            "Watcher hatasından sonra izleme kendiliğinden geri gelmeli.");
        Assert.Equal(1, world.Watcher.WatchedPathCount);
    }

    [Fact]
    public async Task AChangeMadeWhileTheWatcherWasDead_LandsWhenItComesBack()
    {
        using var world = await World.Ready(TimeSpan.FromMilliseconds(600));

        world.Watcher.SimulateWatcherError(new IOException("Test: tampon taştı."));

        Assert.True(
            await world.WaitForRuns(1),
            "Hata anındaki uzlaştırma turu beklendi.");

        var gecGelen = Path.Combine(world.Root, "gec-gelen.txt");
        File.WriteAllText(gecGelen, "watcher ölüyken yazıldı");

        await world.WaitForRevival();

        Assert.True(
            await world.WaitForIndexed(gecGelen),
            "Watcher ölüyken yazılan dosya, izleme geri geldikten sonraki uzlaştırma " +
            "turunda indekse girmeli; ilk tur bu dosyadan önce koştu.");
    }

    [Fact]
    public async Task AFaultedWatcher_BringsThePeriodicNetBack()
    {
        using var world = await World.Ready();
        world.Manager.NoteChangeFeedCoverage(true);

        Assert.True(world.Manager.ChangeFeedGuarding);

        world.Watcher.SimulateWatcherError(new IOException("Test: tampon taştı."));

        Assert.False(
            world.Manager.ChangeFeedGuarding,
            "Watcher öldüyse uygulama çalışırken kör kalır; periyodik tam tarama " +
            "emniyet ağı olarak geri gelmeli.");
    }

    [Fact]
    public async Task RepeatedFaults_RunOnlyOneRevival()
    {
        using var world = await World.Ready();

        world.Watcher.SimulateWatcherError(new IOException("Test: bir."));
        var first = world.Manager.WatcherRevival;

        world.Watcher.SimulateWatcherError(new IOException("Test: iki."));
        var second = world.Manager.WatcherRevival;

        Assert.Same(first, second);

        await world.WaitForRevival();
        Assert.True(world.Manager.IsWatching);
    }

    [Fact]
    public async Task AFaultAfterDisposal_StartsNoRevival()
    {
        var world = await World.Ready();
        var watcher = world.Watcher;
        var manager = world.Manager;

        manager.Dispose();
        watcher.SimulateWatcherError(new IOException("Test: kapanıştan sonra."));

        Assert.Null(manager.WatcherRevival);

        world.Dispose();
    }

    private sealed class World : IDisposable
    {
        private readonly TemporaryDirectory _workspace = new();
        private readonly IndexDatabase _database;

        private World(TimeSpan? revivalDelay = null)
        {
            Root = _workspace.CreateDirectory("kok");
            File.WriteAllText(Path.Combine(Root, "a.txt"), "veri");
            _database = new IndexDatabase(Path.Combine(_workspace.Path, "index.db"));
            Watcher = new FileWatcherService(debounceMs: 1);
            Manager = new IndexManager(
                _database,
                Watcher,
                reconciliationInterval: TimeSpan.FromMinutes(10),
                watcherRevivalDelay: revivalDelay ?? TimeSpan.FromMilliseconds(20));
        }

        public IndexManager Manager { get; }

        public FileWatcherService Watcher { get; }

        public string Root { get; }

        public static async Task<World> Ready(TimeSpan? revivalDelay = null)
        {
            var world = new World(revivalDelay);
            world.Manager.NoteChangeFeedCoverage(true);
            await world.Manager.InitializeAsync(new[] { world.Root });
            return world;
        }

        public async Task WaitForRevival()
        {
            for (var attempt = 0; attempt < 100 && Manager.WatcherRevival is null; attempt++)
            {
                await Task.Delay(10);
            }

            var revival = Manager.WatcherRevival;
            Assert.NotNull(revival);
            await revival!;
        }

        public async Task<bool> WaitForIndexed(string path)
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                if (Manager.GetNode(path) is not null)
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return false;
        }

        public async Task<bool> WaitForRuns(long expected)
        {
            for (var attempt = 0; attempt < 150; attempt++)
            {
                if (Manager.ReconciliationRunCount >= expected)
                {
                    return true;
                }

                await Task.Delay(20);
            }

            return false;
        }

        public void Dispose()
        {
            Manager.Dispose();
            Watcher.Dispose();
            _workspace.Dispose();
        }
    }
}
