using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.ChangeFeedService;

internal sealed class ChangeFeedDrainWorker : BackgroundService
{
    public const string ServiceName = "OmniSpotChangeFeed";
    public const string StoreRootSetting = "StoreRoot";

    private readonly ILogger<ChangeFeedDrainWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _storeRoot;

    public ChangeFeedDrainWorker(
        ILogger<ChangeFeedDrainWorker> logger,
        IHostApplicationLifetime lifetime,
        IConfiguration configuration)
    {
        _logger = logger;
        _lifetime = lifetime;

        var configured = configuration[StoreRootSetting];
        _storeRoot = string.IsNullOrWhiteSpace(configured)
            ? ChangeFeedStoreLayout.DefaultRoot
            : configured;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            await Task.Run(() => Drain(stoppingToken), stoppingToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("Boşaltma durdurma isteğiyle kesildi.");
        }
        finally
        {
            _lifetime.StopApplication();
        }
    }

    private void Drain(CancellationToken cancellationToken)
    {
        var owners = ChangeFeedStoreLayout.EnumerateOwners(_storeRoot);
        if (owners.Count == 0)
        {
            _logger.LogInformation("{Root} altında abone yok.", _storeRoot);
            return;
        }

        foreach (var owner in owners)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DrainOwner(owner, cancellationToken);
        }
    }

    private void DrainOwner(string owner, CancellationToken cancellationToken)
    {
        var layout = ChangeFeedStoreLayout.ForOwner(_storeRoot, owner);

        UsnDrainResult result;
        try
        {
            var runner = new UsnDrainRunner(
                layout,
                new FileSystemChangeFeedStore(layout),
                new UsnVolumeJournalReaderFactory(),
                new UsnFileSystemIdentityProbe());

            result = runner.Run(cancellationToken);
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception failure)
        {
            _logger.LogError(failure, "{Owner} için boşaltma başarısız oldu.", owner);
            return;
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
    }
}
