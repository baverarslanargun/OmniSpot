using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.ChangeFeedService;

internal sealed class ChangeFeedDrainWorker : BackgroundService
{
    public const string ServiceName = ChangeFeedServiceIdentity.ServiceName;

    public static TimeSpan DefaultDrainPeriod => TimeSpan.FromSeconds(15);

    private readonly ILogger<ChangeFeedDrainWorker> _logger;
    private readonly TimeSpan _drainPeriod;
    private readonly Dictionary<string, bool> _passiveOwners =
        new(StringComparer.OrdinalIgnoreCase);

    private int _completedRounds;

    public ChangeFeedDrainWorker(ILogger<ChangeFeedDrainWorker> logger)
        : this(logger, DefaultDrainPeriod)
    {
    }

    internal ChangeFeedDrainWorker(
        ILogger<ChangeFeedDrainWorker> logger,
        TimeSpan drainPeriod)
    {
        _logger = logger;
        _drainPeriod = drainPeriod;
    }

    internal int CompletedRounds => Volatile.Read(ref _completedRounds);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Run(() => Drain(stoppingToken), stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception failure)
            {
                _logger.LogError(
                    failure,
                    "Boşaltma turu başarısız oldu; kök kabulü etkilenmiyor.");
            }

            Interlocked.Increment(ref _completedRounds);

            try
            {
                await Task.Delay(_drainPeriod, stoppingToken).ConfigureAwait(false);
            }
            catch (OperationCanceledException)
            {
                break;
            }
        }

        _logger.LogInformation("Boşaltma döngüsü durdurma isteğiyle bitti.");
    }

    private void Drain(CancellationToken cancellationToken)
    {
        var trustedRoot = ChangeFeedStoreLayout.DefaultTrustedRoot;

        if (ChangeFeedStoreLayout.LegacyStoreExists(ChangeFeedStoreLayout.LegacyRoot))
        {
            _logger.LogWarning(
                "Eski kullanıcı-yazılabilir depo bulundu ve yok sayıldı; içe aktarılmaz.");
        }

        var owners = ChangeFeedStoreLayout.EnumerateOwners(trustedRoot);
        if (owners.Count == 0)
        {
            return;
        }

        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrainOwner(owner, cancellationToken);
        }
    }

    internal static (UsnDrainRunner Runner, IChangeFeedStore Store) CreateRunner(
        ChangeFeedStoreLayout layout)
    {
        ArgumentNullException.ThrowIfNull(layout);

        var store = new FileSystemChangeFeedStore(layout);
        var runner = new UsnDrainRunner(
            layout,
            store,
            new UsnVolumeJournalReaderFactory(),
            new UsnFileSystemIdentityProbe());

        return (runner, store);
    }

    internal static ChangeFeedStoreLayout TrustedLayoutFor(string ownerSid) =>
        ChangeFeedStoreLayout.ForTrustedOwner(ownerSid);

    internal static bool DrainToCurrentBoundary(
        string ownerSid,
        CancellationToken cancellationToken)
    {
        var result = CreateRunner(TrustedLayoutFor(ownerSid)).Runner.Run(cancellationToken);
        return result.Outcome == UsnDrainOutcome.Completed && result.VolumesFaulted == 0;
    }

    internal static bool IsPassive(UsnDrainOutcome outcome) =>
        outcome is UsnDrainOutcome.LeaseHeld or UsnDrainOutcome.LeasePreempted;

    internal static bool IsQuietRound(UsnDrainResult result)
    {
        ArgumentNullException.ThrowIfNull(result);

        return result.Outcome == UsnDrainOutcome.Completed &&
            result.VolumesFaulted == 0 &&
            result.EntriesWritten == 0 &&
            result.EventsWritten == 0 &&
            result.RootsGapped == 0;
    }

    private void NotePassiveTransition(string owner, UsnDrainOutcome outcome)
    {
        var passive = IsPassive(outcome);

        if (_passiveOwners.TryGetValue(owner, out var previous) && previous == passive)
        {
            return;
        }

        _passiveOwners[owner] = passive;

        if (!passive)
        {
            _logger.LogInformation("{Owner}: kira yok; boşaltma devraldı.", owner);
            return;
        }

        if (outcome == UsnDrainOutcome.LeasePreempted)
        {
            _logger.LogInformation(
                "{Owner}: kira tur ortasında geldi; boşaltma bırakıldı.",
                owner);
            return;
        }

        _logger.LogInformation(
            "{Owner}: watcher kirası tutuluyor; boşaltma pasife geçti.",
            owner);
    }

    internal void LogRound(string owner, UsnDrainResult result)
    {
        _logger.Log(
            IsQuietRound(result) ? LogLevel.Debug : LogLevel.Information,
            "{Owner}: {Outcome} birim={Volumes} arizali={Faulted} girdi={Entries} olay={Events} bosluk={Gaps} {Diagnostics}",
            owner,
            result.Outcome,
            result.VolumesDrained,
            result.VolumesFaulted,
            result.EntriesWritten,
            result.EventsWritten,
            result.RootsGapped,
            result.Diagnostics ?? string.Empty);
    }

    internal UsnDrainResult? DrainOwner(string owner, CancellationToken cancellationToken)
    {
        UsnDrainResult result;
        try
        {
            result = CreateRunner(TrustedLayoutFor(owner)).Runner.Run(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "{Owner} için boşaltma başarısız oldu.", owner);
            return null;
        }

        NotePassiveTransition(owner, result.Outcome);

        if (IsPassive(result.Outcome))
        {
            return result;
        }

        LogRound(owner, result);

        return result;
    }
}
