using Microsoft.Extensions.Hosting;
using SmartFileLauncher.ChangeFeedService;

var builder = Host.CreateApplicationBuilder(args);

builder.Services.AddChangeFeedService();

builder.Build().Run();
