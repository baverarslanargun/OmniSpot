using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.EventLog;

namespace SmartFileLauncher.ChangeFeedService;

internal static class ChangeFeedServiceHost
{
    internal const LogLevel EventLogMinimumLevel = LogLevel.Information;

    public static IServiceCollection AddChangeFeedService(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddWindowsService(options =>
            options.ServiceName = ChangeFeedDrainWorker.ServiceName);

        services.AddLogging(logging =>
            logging.AddFilter<EventLogLoggerProvider>(
                level => level >= EventLogMinimumLevel));

        services.AddHostedService<ChangeFeedAdmissionWorker>();
        services.AddHostedService<ChangeFeedDrainWorker>();

        return services;
    }
}
