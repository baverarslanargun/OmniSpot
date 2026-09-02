using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.ChangeFeedService;

internal sealed class ChangeFeedAdmissionWorker : BackgroundService
{
    private readonly ILogger<ChangeFeedAdmissionWorker> _logger;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly string _pipeName;

    public ChangeFeedAdmissionWorker(
        ILogger<ChangeFeedAdmissionWorker> logger,
        IHostApplicationLifetime lifetime)
        : this(logger, lifetime, ChangeFeedProtocol.PipeName)
    {
    }

    internal ChangeFeedAdmissionWorker(
        ILogger<ChangeFeedAdmissionWorker> logger,
        IHostApplicationLifetime lifetime,
        string pipeName)
    {
        _logger = logger;
        _lifetime = lifetime;
        _pipeName = pipeName;
    }

    public static ChangeFeedAdmissionService CreateAdmissionService() =>
        new(
            new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()),
            ownerSid => new FileSystemChangeFeedStore(
                ChangeFeedStoreLayout.ForTrustedOwner(ownerSid)));

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var server = new ChangeFeedPipeServer(
            CreateAdmissionService(),
            _pipeName,
            additionalServerPrincipal: null,
            onFault: failure => _logger.LogWarning(
                "Kabul bağlantısı hatayla kapandı: {Reason}",
                failure.GetType().Name));

        try
        {
            _logger.LogInformation(
                "{Pipe} kanalında kök kabulü dinleniyor.",
                _pipeName);

            await server.ListenAsync(stoppingToken).ConfigureAwait(false);

            _logger.LogCritical("Kök kabulü dinleyicisi beklenmedik biçimde sonlandı.");
            _lifetime.StopApplication();
        }
        catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
        {
            _logger.LogInformation("Kök kabulü durdurma isteğiyle kesildi.");
        }
        catch (ChangeFeedPipeException failure)
        {
            _logger.LogCritical(
                failure,
                "{Pipe} kanalı kullanılamıyor; servis kök kabulü yapamaz.",
                _pipeName);
            _lifetime.StopApplication();
        }
        catch (Exception failure)
        {
            _logger.LogCritical(failure, "Kök kabulü beklenmedik biçimde durdu.");
            _lifetime.StopApplication();
        }
    }
}
