using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using Xunit;

namespace SmartFileLauncher.ChangeFeedService.Tests;

public sealed class ChangeFeedServiceLifecycleTests
{
    [Fact]
    public async Task DrainWorker_LetsNoExceptionEscapeExecuteAsync()
    {
        var worker = new ChangeFeedDrainWorker(NullLogger<ChangeFeedDrainWorker>.Instance);

        await worker.StartAsync(CancellationToken.None);

        Assert.NotNull(worker.ExecuteTask);
        await worker.ExecuteTask!;

        Assert.Equal(TaskStatus.RanToCompletion, worker.ExecuteTask!.Status);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public async Task AdmissionWorker_StopsTheHostWhenThePipeNameCannotBeTaken()
    {
        var pipeName = "OmniSpot.Test." + Guid.NewGuid().ToString("N");
        using var squatter = ChangeFeedPipeFactory.CreateFirstInstance(pipeName);

        var lifetime = new RecordingLifetime();
        var worker = new ChangeFeedAdmissionWorker(
            NullLogger<ChangeFeedAdmissionWorker>.Instance,
            lifetime,
            pipeName);

        await worker.StartAsync(CancellationToken.None);
        await worker.ExecuteTask!;

        Assert.True(lifetime.Stopped);

        await worker.StopAsync(CancellationToken.None);
    }

    [Fact]
    public void DrainOwner_GoesThroughTheTrustedLayoutAndRefusesAnythingElse()
    {
        var logger = new CapturingLogger();
        var worker = new ChangeFeedDrainWorker(logger);

        var result = worker.DrainOwner("S-1-5-21-1-2-3-1001", CancellationToken.None);

        Assert.Null(result);
        Assert.Contains(
            logger.Failures,
            failure => failure is ChangeFeedStoreSecurityException
                && failure.Message.Contains("LocalSystem", StringComparison.Ordinal));
    }

    private sealed class RecordingLifetime : IHostApplicationLifetime
    {
        private readonly CancellationTokenSource _stopping = new();

        public bool Stopped { get; private set; }

        public CancellationToken ApplicationStarted => CancellationToken.None;

        public CancellationToken ApplicationStopping => _stopping.Token;

        public CancellationToken ApplicationStopped => CancellationToken.None;

        public void StopApplication()
        {
            Stopped = true;
            _stopping.Cancel();
        }
    }

    private sealed class CapturingLogger : ILogger<ChangeFeedDrainWorker>
    {
        public List<Exception> Failures { get; } = new();

        public IDisposable? BeginScope<TState>(TState state)
            where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter)
        {
            if (exception is not null)
            {
                Failures.Add(exception);
            }
        }
    }
}
