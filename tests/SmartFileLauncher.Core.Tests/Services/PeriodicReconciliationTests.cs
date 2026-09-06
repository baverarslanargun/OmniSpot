using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class PeriodicReconciliationTests
{
    [Fact]
    public void WhenTheChangeFeedGuards_ThereIsNoPeriodicPass()
    {
        using var world = new World(TimeSpan.FromMilliseconds(200));
        world.Manager.NoteChangeFeedCoverage(true);

        Assert.Equal(Timeout.InfiniteTimeSpan, world.Manager.NextReconciliationDelay());
    }

    [Fact]
    public void WithoutTheChangeFeed_ThePeriodicPassRemains()
    {
        using var world = new World(TimeSpan.FromMilliseconds(200));

        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            world.Manager.NextReconciliationDelay());

        world.Manager.NoteChangeFeedCoverage(false);

        Assert.Equal(
            TimeSpan.FromMilliseconds(200),
            world.Manager.NextReconciliationDelay());
    }

    [Fact]
    public async Task TheGuardOutlivesTheConsumedStartupFlag()
    {
        using var world = new World(TimeSpan.FromMilliseconds(200));
        world.Manager.NoteChangeFeedCoverage(true);

        await world.Manager.InitializeAsync(new[] { world.Root });

        Assert.False(world.Manager.ChangeFeedCoversDowntime);
        Assert.True(
            world.Manager.ChangeFeedGuarding,
            "Nöbet bayrağı tek kullanımlık olmamalı; akış hâlâ görevdeyken " +
            "periyodik tam tarama geri gelmemeli.");
    }

    [Fact]
    public async Task WithoutTheChangeFeed_TheScanKeepsRepeating()
    {
        using var world = new World(TimeSpan.FromMilliseconds(150));

        await world.Manager.InitializeAsync(new[] { world.Root });

        Assert.True(
            await world.WaitForRuns(3),
            $"Akış görevde değilken periyodik tarama sürmeli; " +
            $"görülen tur: {world.Manager.ReconciliationRunCount}");
    }

    [Fact]
    public async Task WhenTheChangeFeedGuards_TheScanNeverRepeats()
    {
        using var world = new World(TimeSpan.FromMilliseconds(150));
        world.Manager.NoteChangeFeedCoverage(true);

        await world.Manager.InitializeAsync(new[] { world.Root });
        await Task.Delay(1200);

        Assert.Equal(0, world.Manager.ReconciliationRunCount);
    }

    [Fact]
    public async Task AWatcherFailure_StillForcesAScanWhileTheFeedGuards()
    {
        using var world = new World(TimeSpan.FromMilliseconds(150));
        world.Manager.NoteChangeFeedCoverage(true);

        await world.Manager.InitializeAsync(new[] { world.Root });
        await Task.Delay(300);
        Assert.Equal(0, world.Manager.ReconciliationRunCount);

        world.Watcher.SimulateWatcherError(new IOException("Test: tampon taştı."));

        Assert.True(
            await world.WaitForRuns(1),
            "Periyodik tur kalkınca emniyet ağı olaya bağlı olmalı: watcher " +
            "hatası tam taramayı tetiklemeli.");
    }

    [Fact]
    public async Task WhenTheFeedIsLost_ThePeriodicPassComesBack()
    {
        using var world = new World(TimeSpan.FromMilliseconds(150));
        world.Manager.NoteChangeFeedCoverage(true);

        await world.Manager.InitializeAsync(new[] { world.Root });
        await Task.Delay(300);
        Assert.Equal(0, world.Manager.ReconciliationRunCount);

        world.Manager.NoteChangeFeedLost();

        Assert.False(world.Manager.ChangeFeedGuarding);
        Assert.True(
            await world.WaitForRuns(3),
            "Akış nöbeti düşünce periyodik tam tarama emniyet ağı olarak geri gelmeli; " +
            $"görülen tur: {world.Manager.ReconciliationRunCount}");
    }

    private sealed class World : IDisposable
    {
        private readonly TemporaryDirectory _workspace = new();
        private readonly IndexDatabase _database;

        public World(TimeSpan interval)
        {
            Root = _workspace.CreateDirectory("kok");
            File.WriteAllText(Path.Combine(Root, "a.txt"), "veri");
            _database = new IndexDatabase(Path.Combine(_workspace.Path, "index.db"));
            Watcher = new FileWatcherService(debounceMs: 1);
            Manager = new IndexManager(_database, Watcher, reconciliationInterval: interval);
        }

        public IndexManager Manager { get; }

        public FileWatcherService Watcher { get; }

        public string Root { get; }

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
