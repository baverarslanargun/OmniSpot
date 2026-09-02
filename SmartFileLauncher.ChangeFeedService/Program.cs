using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Hosting.WindowsServices;
using SmartFileLauncher.ChangeFeedService;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddWindowsService(options =>
    options.ServiceName = ChangeFeedDrainWorker.ServiceName);

builder.Services.AddHostedService<ChangeFeedDrainWorker>();

builder.Build().Run();
