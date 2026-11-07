using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using System.Runtime.Versioning;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace OmniSpot.Benchmarking.Ntfs;

internal static class SharedFirstReadyServiceHost
{
    internal static async Task<int> RunAsync(string runRoot, string pipeName,
        string serviceName, string ownerSid, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        try
        {
            RequireLocalSystem();
            SharedFirstReadyCommand.ValidateServiceIdentity(serviceName, pipeName);
            runRoot = SharedFirstReadyCommand.RequireServiceRunRoot(runRoot);
            _ = new SecurityIdentifier(ownerSid);

            var builder = Host.CreateApplicationBuilder();
            builder.Services.AddWindowsService(options => options.ServiceName = serviceName);
            var options = new ServiceOptions(runRoot, pipeName, serviceName, ownerSid);
            builder.Services.AddSingleton(options);
            builder.Services.AddSingleton(new IsolatedRuntime(options));
            builder.Services.AddHostedService<BenchmarkPipeWorker>();
            builder.Services.AddHostedService<BenchmarkDrainWorker>();
            using var host = builder.Build();
            await host.RunAsync(cancellationToken).ConfigureAwait(false);
            return 0;
        }
        catch (Exception failure)
        {
            Console.Error.WriteLine(failure.GetType().Name);
            return 1;
        }
    }

    [SupportedOSPlatform("windows")]
    private static void RequireLocalSystem()
    {
        using var identity = WindowsIdentity.GetCurrent();
        var expected = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (identity.User is null || !identity.User.Equals(expected))
            throw new UnauthorizedAccessException("LocalSystemTokenRequired");
    }

    private sealed record ServiceOptions(string RunRoot, string PipeName, string ServiceName, string OwnerSid);

    [SupportedOSPlatform("windows")]
    private sealed class IsolatedRuntime
    {
        internal IsolatedRuntime(ServiceOptions options)
        {
            var storeRoot = SharedFirstReadyCommand.ChildPath(options.RunRoot, "trusted-store");
            var serviceSid = ChangeFeedServiceIdentity.DeriveServiceSid(options.ServiceName);
            Layout = ChangeFeedStoreLayout.ForTrustedOwner(storeRoot, options.OwnerSid, serviceSid);
            var store = new FileSystemChangeFeedStore(Layout);
            Runner = new UsnDrainRunner(Layout, store,
                new UsnVolumeJournalReaderFactory(), new UsnFileSystemIdentityProbe(), cacheState: true);
            ServiceSid = new SecurityIdentifier(serviceSid);
        }

        internal ChangeFeedStoreLayout Layout { get; }
        internal UsnDrainRunner Runner { get; }
        internal SecurityIdentifier ServiceSid { get; }
    }

    private sealed class BenchmarkPipeWorker(ServiceOptions options, IsolatedRuntime runtime) : BackgroundService
    {
        [SupportedOSPlatform("windows")]
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            ChangeFeedStoreLayout LayoutFor(string owner) =>
                string.Equals(owner, options.OwnerSid, StringComparison.OrdinalIgnoreCase)
                    ? runtime.Layout
                    : throw new UnauthorizedAccessException("OwnerMismatch");
            IChangeFeedStore StoreFor(string owner) =>
                string.Equals(owner, options.OwnerSid, StringComparison.OrdinalIgnoreCase)
                    ? new FileSystemChangeFeedStore(runtime.Layout)
                    : throw new UnauthorizedAccessException("OwnerMismatch");
            using var admission = new ChangeFeedAdmissionService(
                new ChangeFeedRootAdmission(new UsnFileSystemIdentityProbe()), StoreFor,
                handoffDrainer: (_, ct) =>
                {
                    var result = runtime.Runner.Run(ct);
                    return result.Outcome == UsnDrainOutcome.Completed && result.VolumesFaulted == 0;
                },
                inventory: new ChangeFeedInventorySessions(),
                sharedCatalogLayout: LayoutFor,
                catalogBootstrap: (_, ct) => runtime.Runner.BeginBootstrap(ct));
            var server = new ChangeFeedPipeServer(admission, options.PipeName,
                runtime.ServiceSid);
            await server.ListenAsync(stoppingToken).ConfigureAwait(false);
        }
    }

    private sealed class BenchmarkDrainWorker(IsolatedRuntime runtime,
        ILogger<BenchmarkDrainWorker> logger) : BackgroundService
    {
        [SupportedOSPlatform("windows")]
        protected override async Task ExecuteAsync(CancellationToken stoppingToken)
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    if (Directory.Exists(runtime.Layout.OwnerDirectory))
                        await Task.Run(() => runtime.Runner.Run(stoppingToken), stoppingToken)
                            .ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    break;
                }
                catch (Exception failure)
                {
                    logger.LogError(failure, "Geçici ölçüm USN boşaltma turu başarısız oldu.");
                }

                try { await Task.Delay(TimeSpan.FromSeconds(15), stoppingToken).ConfigureAwait(false); }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested) { break; }
            }
        }
    }
}
