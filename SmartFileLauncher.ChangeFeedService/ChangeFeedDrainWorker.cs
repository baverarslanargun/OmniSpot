using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.ChangeFeedService;

internal sealed class ChangeFeedDrainWorker : BackgroundService
{
    public const string ServiceName = ChangeFeedServiceIdentity.ServiceName;

    private readonly ILogger<ChangeFeedDrainWorker> _logger;

    public ChangeFeedDrainWorker(ILogger<ChangeFeedDrainWorker> logger)
    {
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Run(() => Drain(stoppingToken), stoppingToken).ConfigureAwait(false);
            _logger.LogInformation("Boşaltma turu bitti; servis kök kabulü için açık kalıyor.");
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Boşaltma durdurma isteğiyle kesildi.");
        }
        catch (Exception failure)
        {
            _logger.LogError(
                failure,
                "Boşaltma turu başarısız oldu; kök kabulü etkilenmiyor.");
        }
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
            _logger.LogInformation("Güvenilir depoda abone yok.");
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

        _logger.LogInformation(
            "{Owner}: {Outcome} birim={Volumes} arizali={Faulted} girdi={Entries} olay={Events} bosluk={Gaps} {Diagnostics}",
            owner,
            result.Outcome,
            result.VolumesDrained,
            result.VolumesFaulted,
            result.EntriesWritten,
            result.EventsWritten,
            result.RootsGapped,
            result.Diagnostics ?? string.Empty);

        return result;
    }
}
