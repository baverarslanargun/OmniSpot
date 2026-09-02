using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;

namespace SmartFileLauncher.ChangeFeedService;

internal static class ChangeFeedServiceHost
{
    public static IServiceCollection AddChangeFeedService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddWindowsService(options =>
            options.ServiceName = ChangeFeedDrainWorker.ServiceName);

        services.AddHostedService<ChangeFeedAdmissionWorker>();
        services.AddHostedService<ChangeFeedDrainWorker>();

        return services;
    }
}
