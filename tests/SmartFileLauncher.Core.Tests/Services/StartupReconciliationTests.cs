using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class StartupReconciliationTests
{
    [Theory]
    [InlineData(ChangeFeedAdoptionStatus.Adopted, true, 6, 6, true)]
    [InlineData(ChangeFeedAdoptionStatus.NothingToAdopt, true, 6, 6, true)]
    [InlineData(ChangeFeedAdoptionStatus.Adopted, false, 6, 6, false)]
    [InlineData(ChangeFeedAdoptionStatus.Incomplete, true, 6, 6, false)]
    [InlineData(ChangeFeedAdoptionStatus.ServiceUnavailable, false, 0, 6, false)]
    [InlineData(ChangeFeedAdoptionStatus.Refused, false, 0, 6, false)]
    [InlineData(ChangeFeedAdoptionStatus.Adopted, true, 5, 6, false)]
    public void OnlyACleanAdoptionCoversTheDowntime(
        ChangeFeedAdoptionStatus status,
        bool leaseHeld,
        int subscribed,
        int rootCount,
        bool covers)
    {
        var adoption = new ChangeFeedAdoptionResult(
            status,
            subscribed,
            0,
            0,
            0,
            leaseHeld);

        Assert.Equal(covers, IndexLifecycleService.CoversDowntime(adoption, rootCount));
    }

    [Fact]
    public void AMissingAdoptionNeverCoversTheDowntime()
    {
        Assert.False(IndexLifecycleService.CoversDowntime(null, 6));
    }

    [Fact]
    public async Task WithoutTheChangeFeed_StartupStillReconciles()
    {
        using var world = new World();

        await world.Manager.InitializeAsync(new[] { world.Root });
        await world.WaitForReconciliation();

        Assert.True(
            world.Manager.ReconciliationRunCount >= 1,
            "Akış kapsamı bildirilmediyse açılış taraması çalışmalı.");
    }

    [Fact]
    public async Task WhenTheFeedCoversTheDowntime_StartupSkipsTheScan()
    {
        using var world = new World();
        world.Manager.NoteChangeFeedCoverage(true);

        await world.Manager.InitializeAsync(new[] { world.Root });
        await Task.Delay(600);

        Assert.Equal(0, world.Manager.ReconciliationRunCount);
    }

    [Fact]
    public async Task TheCoverageFlag_IsConsumedByTheFirstReconciliationStart()
    {
        using var world = new World();
        world.Manager.NoteChangeFeedCoverage(true);

        await world.Manager.InitializeAsync(new[] { world.Root });

        Assert.False(
            world.Manager.ChangeFeedCoversDowntime,
            "Kapsam bayrağı tek kullanımlık olmalı; aksi halde sonraki bir yeniden " +
            "tarama devralma yapılmadığı hâlde açılış taramasını atlar.");
    }

    private sealed class World : IDisposable
    {
        private readonly TemporaryDirectory _workspace = new();
        private readonly FileWatcherService _watcher = new(debounceMs: 1);
        private readonly IndexDatabase _database;

        public World()
        {
            Root = _workspace.CreateDirectory("kok");
            File.WriteAllText(Path.Combine(Root, "a.txt"), "veri");
            _database = new IndexDatabase(Path.Combine(_workspace.Path, "index.db"));
            Manager = new IndexManager(_database, _watcher);
        }

        public IndexManager Manager { get; }

        public string Root { get; }

        public async Task WaitForReconciliation()
        {
            for (var attempt = 0; attempt < 100; attempt++)
            {
                if (Manager.ReconciliationRunCount >= 1)
                {
                    return;
                }

                await Task.Delay(20);
            }
        }

        public void Dispose()
        {
            Manager.Dispose();
            _watcher.Dispose();
            _workspace.Dispose();
        }
    }
}
