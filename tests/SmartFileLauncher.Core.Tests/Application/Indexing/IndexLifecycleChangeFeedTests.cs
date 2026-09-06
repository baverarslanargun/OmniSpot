using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class IndexLifecycleChangeFeedTests
{
    [Fact]
    public async Task Initialize_AdoptsTheFeedAndKeepsTheLease()
    {
        using var world = new World();
        var channel = world.Channel;

        await world.Lifecycle.InitializeAsync();

        Assert.Equal(
            new[]
            {
                ChangeFeedRequestKind.AddRoot,
                ChangeFeedRequestKind.DrainAndHoldLease,
                ChangeFeedRequestKind.Pull
            },
            channel.Kinds);

        var adoption = world.Lifecycle.LastAdoption;
        Assert.NotNull(adoption);
        Assert.True(adoption!.LeaseHeld);
        Assert.Equal(1, adoption.RootsSubscribed);
    }

    [Fact]
    public async Task Dispose_HandsTheFeedBackToTheService()
    {
        var world = new World();
        await world.Lifecycle.InitializeAsync();

        world.Dispose();

        Assert.Equal(ChangeFeedRequestKind.ReleaseLease, world.Channel.Kinds[^1]);
    }

    [Fact]
    public async Task AWatcherFault_HandsTheFeedBackToTheService()
    {
        using var world = new World();
        await world.Lifecycle.InitializeAsync();
        world.Channel.Clear();

        world.BreakTheWatcher();

        await world.WaitForKind(ChangeFeedRequestKind.ReleaseLease);
    }

    [Fact]
    public async Task RepeatedWatcherFaults_HandOverOnlyOnce()
    {
        using var world = new World();
        await world.Lifecycle.InitializeAsync();
        world.Channel.Clear();

        world.BreakTheWatcher();
        await world.WaitForKind(ChangeFeedRequestKind.ReleaseLease);

        world.BreakTheWatcher();
        world.BreakTheWatcher();
        await Task.Delay(300);

        Assert.Equal(1, world.Channel.Count(ChangeFeedRequestKind.ReleaseLease));
    }

    [Fact]
    public async Task AfterAWatcherFault_TheLeaseIsNoLongerRenewed()
    {
        using var world = new World(renewInterval: TimeSpan.FromMilliseconds(40));
        await world.Lifecycle.InitializeAsync();

        await world.WaitForKind(ChangeFeedRequestKind.HoldLease, atLeast: 2);
        world.BreakTheWatcher();
        await world.WaitForKind(ChangeFeedRequestKind.ReleaseLease);

        var renewals = world.Channel.Count(ChangeFeedRequestKind.HoldLease);
        await Task.Delay(300);

        Assert.Equal(renewals, world.Channel.Count(ChangeFeedRequestKind.HoldLease));
    }

    [Fact]
    public async Task ANonFatalDispatchError_DoesNotHandTheFeedOver()
    {
        using var world = new World();
        await world.Lifecycle.InitializeAsync();
        world.Channel.Clear();
        world.MakeDispatchThrow();

        world.Write("yeni.txt");
        await world.WaitForError("dağıtım");
        await Task.Delay(300);

        Assert.True(
            world.Channel.Count(ChangeFeedRequestKind.ReleaseLease) == 0,
            "Tek bir dağıtım hatası bütün feed'i devretmemeli.");
    }

    [Fact]
    public async Task Watcher_CapturesDuringTheFinalDrainAndDispatchesAfterAdoption()
    {
        using var world = new World(observeWatching: true);

        await world.Lifecycle.InitializeAsync();

        Assert.True(
            world.WatchingDuringAdoption,
            "Son USN boşaltması sırasında watcher değişiklik toplamıyor.");
        Assert.True(
            world.DispatchPausedDuringAdoption,
            "Son USN boşaltması bitmeden watcher olayları indekse uygulanmamalı.");
        Assert.False(
            world.IndexedDuringAdoption,
            "Watcher olayı devir çizgisinden önce indekse sızdı.");
        await world.WaitForIndexed("devriyakala");
        Assert.True(world.Lifecycle.IsInitialized);
    }

    [Fact]
    public async Task AHangingHandOver_DoesNotBlockDisposeBeyondItsBudget()
    {
        var world = new World(hangOnRelease: true);
        await world.Lifecycle.InitializeAsync();

        var elapsed = System.Diagnostics.Stopwatch.StartNew();
        world.Dispose();
        elapsed.Stop();

        Assert.True(
            elapsed.Elapsed < TimeSpan.FromSeconds(4),
            $"Kapanış {elapsed.Elapsed.TotalSeconds:F1} saniye sürdü.");
    }

    [Fact]
    public async Task AFailedFaultHandOver_IsReportedAndDoesNotThrow()
    {
        using var world = new World(hangOnRelease: true);
        await world.Lifecycle.InitializeAsync();

        world.BreakTheWatcher();
        var handOver = world.Lifecycle.WatcherFaultHandOver;

        Assert.NotNull(handOver);
        await handOver!;
        await world.WaitForError("doğrulanamadı");
        lock (world.Errors)
        {
            Assert.Contains(
                world.Errors,
                message => message.Contains("doğrulanamadı", StringComparison.OrdinalIgnoreCase));
        }

        Assert.False(world.Lifecycle.LeaseReleaseConfirmed);
        {
        }
    }

    [Fact]
    public async Task AFailedFaultHandOver_BringsThePeriodicScanBack()
    {
        using var world = new World(hangOnRelease: true);
        await world.Lifecycle.InitializeAsync();

        Assert.True(
            world.Manager.ChangeFeedGuarding,
            "Temiz devralmadan sonra akış nöbette olmalı.");

        world.BreakTheWatcher();
        var handOver = world.Lifecycle.WatcherFaultHandOver;

        Assert.NotNull(handOver);
        await handOver!;

        Assert.False(
            world.Manager.ChangeFeedGuarding,
            "Watcher öldü ve devir de başarısız olduysa indeksi koruyan hiçbir şey " +
            "kalmaz; periyodik tam tarama geri gelmeli.");
    }

    [Fact]
    public async Task AGapDuringStartup_IsReconciledThroughTheRealIndex()
    {
        using var world = new World(realTarget: true, gapOnFirstPull: true);

        await world.Lifecycle.InitializeAsync();

        var adoption = world.Lifecycle.LastAdoption;
        Assert.NotNull(adoption);
        Assert.Equal(ChangeFeedAdoptionStatus.Adopted, adoption!.Status);
        Assert.Equal(1, adoption.RootsResynchronized);
        Assert.True(adoption.LeaseHeld, "Boşluk uzlaştırıldıysa kira alınmalıydı.");
    }

    [Fact]
    public async Task AFailedRenewal_AlsoReleasesTheLease()
    {
        using var world = new World(
            renewInterval: TimeSpan.FromMilliseconds(40),
            failRenewals: true);

        await world.Lifecycle.InitializeAsync();

        await world.WaitForKind(ChangeFeedRequestKind.ReleaseLease);
        await world.WaitForError("yenilenemedi");
    }

    [Fact]
    public async Task AnUnreachableService_DoesNotStopStartup()
    {
        using var world = new World(unreachable: true);

        var result = await world.Lifecycle.InitializeAsync();

        Assert.NotNull(result);
        Assert.True(world.Lifecycle.IsInitialized);
        Assert.Equal(
            ChangeFeedAdoptionStatus.ServiceUnavailable,
            world.Lifecycle.LastAdoption!.Status);
    }

    [Fact]
    public async Task CancelledAdoption_LeavesNoPausedWatcherBehind()
    {
        using var world = new World();
        using var cancellation = new CancellationTokenSource();
        world.Channel.Observe(request =>
        {
            if (request.Kind == ChangeFeedRequestKind.AddRoot)
            {
                cancellation.Cancel();
            }
        });

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => world.Lifecycle.InitializeAsync(cancellation.Token));

        Assert.False(world.IsWatching);
        Assert.False(world.Lifecycle.IsInitialized);
    }

    [Fact]
    public async Task WithoutABridge_StartupIsUnchanged()
    {
        using var world = new World(withBridge: false);

        await world.Lifecycle.InitializeAsync();

        Assert.True(world.Lifecycle.IsInitialized);
        Assert.Null(world.Lifecycle.LastAdoption);
        Assert.Empty(world.Channel.Kinds);
    }

    private sealed class World : IDisposable
    {
        private readonly TemporaryDirectory _workspace = new();

        private readonly FileWatcherService _watcher;

        private readonly IndexManager _manager;
        private readonly string _root;

        public IndexManager Manager => _manager;

        public World(
            bool withBridge = true,
            bool unreachable = false,
            TimeSpan? renewInterval = null,
            bool observeWatching = false,
            bool hangOnRelease = false,
            bool realTarget = false,
            bool gapOnFirstPull = false,
            bool failRenewals = false)
        {
            var root = _workspace.CreateDirectory("kok");
            _root = root;
            File.WriteAllText(Path.Combine(root, "rapor.txt"), "içerik");

            Channel = new ScriptedChannel(unreachable, hangOnRelease, gapOnFirstPull, failRenewals);
            _watcher = new FileWatcherService(debounceMs: 1);
            _manager = new IndexManager(
                new IndexDatabase(Path.Combine(_workspace.Path, "index.db")),
                _watcher);
            _manager.OnError += message =>
            {
                lock (Errors)
                {
                    Errors.Add(message);
                }
            };

            if (observeWatching)
            {
                Channel.Observe(request =>
                {
                    if (request.Kind != ChangeFeedRequestKind.DrainAndHoldLease ||
                        WatchingDuringAdoption)
                    {
                        return;
                    }

                    WatchingDuringAdoption = _manager.IsWatching;
                    DispatchPausedDuringAdoption = _watcher.IsDispatchPaused;

                    var path = Write("devriyakala.txt");
                    _watcher.TriggerEvent(new FileChangeEvent
                    {
                        ChangeType = FileChangeType.Created,
                        FullPath = path,
                        IsDirectory = false
                    });

                    Thread.Sleep(50);
                    IndexedDuringAdoption = _manager.CurrentSearchState
                        .Get("devriyakala")
                        .Any();
                });
            }

            Lifecycle = new IndexLifecycleService(
                _manager,
                new FixedLocations(root),
                withBridge
                    ? new ChangeFeedIndexBridge(
                        Channel,
                        realTarget
                            ? new IndexManagerChangeFeedTarget(_manager)
                            : new NullTarget())
                    : null,
                renewInterval);
        }

        public List<string> Errors { get; } = new();

        public bool WatchingDuringAdoption { get; private set; }

        public bool DispatchPausedDuringAdoption { get; private set; }

        public bool IndexedDuringAdoption { get; private set; }

        public bool IsWatching => _watcher.IsWatching;

        public void BreakTheWatcher() =>
            _watcher.TriggerFault(new IOException("Test: izleyici düştü."));

        public void MakeDispatchThrow() =>
            _watcher.OnChange += _ =>
                throw new IOException("Test: dağıtım geri çağrısı düştü.");

        public string Write(string name)
        {
            var path = Path.Combine(_root, name);
            File.WriteAllText(path, "içerik");
            return path;
        }

        public async Task WaitForError(string fragment)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                lock (Errors)
                {
                    if (Errors.Any(message =>
                            message.Contains(fragment, StringComparison.OrdinalIgnoreCase)))
                    {
                        return;
                    }
                }

                await Task.Delay(20);
            }

            Assert.Fail($"'{fragment}' içeren bir hata beklenmişti.");
        }

        public async Task WaitForKind(ChangeFeedRequestKind kind, int atLeast = 1)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (Channel.Count(kind) < atLeast && DateTime.UtcNow < deadline)
            {
                await Task.Delay(20);
            }

            Assert.True(
                Channel.Count(kind) >= atLeast,
                $"{kind} en az {atLeast} kez beklendi, {Channel.Count(kind)} geldi.");
        }

        public async Task WaitForIndexed(string token)
        {
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (DateTime.UtcNow < deadline)
            {
                if (_manager.CurrentSearchState.Get(token).Any())
                {
                    return;
                }

                await Task.Delay(20);
            }

            Assert.Fail($"'{token}' watcher devrinden sonra indekse girmedi.");
        }

        public ScriptedChannel Channel { get; }

        public IndexLifecycleService Lifecycle { get; }

        public void Dispose()
        {
            Lifecycle.Dispose();
            _workspace.Dispose();
        }
    }

    private sealed class ScriptedChannel : IChangeFeedRequestChannel
    {
        private readonly bool _unreachable;
        private readonly bool _hangOnRelease;
        private readonly bool _gapOnFirstPull;
        private readonly bool _failRenewals;
        private Action<ChangeFeedRequest>? _observer;
        private int _pulls;
        private int _holds;
        private string _root = string.Empty;

        public ScriptedChannel(
            bool unreachable,
            bool hangOnRelease = false,
            bool gapOnFirstPull = false,
            bool failRenewals = false)
        {
            _unreachable = unreachable;
            _hangOnRelease = hangOnRelease;
            _gapOnFirstPull = gapOnFirstPull;
            _failRenewals = failRenewals;
        }

        public void Observe(Action<ChangeFeedRequest> observer) => _observer = observer;

        public List<ChangeFeedRequestKind> Kinds { get; } = new();

        public int Count(ChangeFeedRequestKind kind)
        {
            lock (Kinds)
            {
                return Kinds.Count(entry => entry == kind);
            }
        }

        public void Clear()
        {
            lock (Kinds)
            {
                Kinds.Clear();
            }
        }

        public Task<ChangeFeedResponse> SendAsync(
            ChangeFeedRequest request,
            CancellationToken cancellationToken)
        {
            lock (Kinds)
            {
                Kinds.Add(request.Kind);
            }

            _observer?.Invoke(request);

            if (_unreachable && request.Kind != ChangeFeedRequestKind.AddRoot)
            {
                throw new IOException("Test: servis yok.");
            }

            if (_hangOnRelease && request.Kind == ChangeFeedRequestKind.ReleaseLease)
            {
                cancellationToken.WaitHandle.WaitOne(TimeSpan.FromSeconds(30));
                cancellationToken.ThrowIfCancellationRequested();
            }

            if (request.Kind == ChangeFeedRequestKind.AddRoot && request.RootPath is { } added)
            {
                _root = added;
            }

            if (request.Kind == ChangeFeedRequestKind.HoldLease &&
                _failRenewals &&
                Interlocked.Increment(ref _holds) >= 1)
            {
                return Task.FromResult(ChangeFeedResponse.Failed(
                    ChangeFeedResponseStatus.Unavailable,
                    "Test: kira yenilenemedi."));
            }

            if (request.Kind != ChangeFeedRequestKind.Pull)
            {
                return Task.FromResult(ChangeFeedResponse.Ok());
            }

            var roots = _gapOnFirstPull && Interlocked.Increment(ref _pulls) == 1
                ? new[]
                {
                    new ChangeFeedRootPageDto(
                        _root,
                        Array.Empty<ChangeFeedEventDto>(),
                        ChangeFeedGapReason.NotYetSynchronized,
                        ChangeFeedFaultReason.None,
                        false,
                        false)
                }
                : Array.Empty<ChangeFeedRootPageDto>();

            return Task.FromResult(ChangeFeedResponse.Delivered(
                new ChangeFeedDeliveryDto(roots, false, null, null)));
        }
    }

    private sealed class NullTarget : IChangeFeedIndexTarget
    {
        public bool Apply(IReadOnlyList<FileChangeEvent> changes) => true;

        public Task<bool> ResynchronizeAsync(
            string rootPath,
            bool withinLifecycle,
            CancellationToken cancellationToken) => Task.FromResult(true);
    }

    private sealed class FixedLocations : IIndexedLocationProvider
    {
        private readonly string _root;

        public FixedLocations(string root) => _root = root;

        public IndexLocations Resolve() => new(_root, new[] { _root });
    }
}
