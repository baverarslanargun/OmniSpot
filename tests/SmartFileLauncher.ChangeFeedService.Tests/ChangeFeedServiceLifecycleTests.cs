using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Logging.EventLog;
using Microsoft.Extensions.Options;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;
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

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(TaskStatus.RanToCompletion, worker.ExecuteTask!.Status);
    }

    [Fact]
    public async Task DrainWorker_KeepsTakingRoundsUntilItIsStopped()
    {
        var worker = new ChangeFeedDrainWorker(
            NullLogger<ChangeFeedDrainWorker>.Instance,
            TimeSpan.FromMilliseconds(15));

        await worker.StartAsync(CancellationToken.None);
        await Task.Delay(400);

        Assert.False(
            worker.ExecuteTask!.IsCompleted,
            "Servis tek turdan sonra boşaltmayı bırakmamalı; devralma buna bağlı.");
        Assert.True(
            worker.CompletedRounds > 1,
            $"Birden çok tur beklenmişti, {worker.CompletedRounds} tur yapıldı.");

        await worker.StopAsync(CancellationToken.None);

        Assert.Equal(TaskStatus.RanToCompletion, worker.ExecuteTask!.Status);
    }

    [Theory]
    [InlineData(UsnDrainOutcome.Completed, 0, 0, 0, 0, true)]
    [InlineData(UsnDrainOutcome.Completed, 1, 0, 0, 0, false)]
    [InlineData(UsnDrainOutcome.Completed, 1, 30, 0, 0, false)]
    [InlineData(UsnDrainOutcome.Completed, 0, 0, 1, 0, false)]
    [InlineData(UsnDrainOutcome.Completed, 0, 0, 0, 1, false)]
    [InlineData(UsnDrainOutcome.Faulted, 0, 0, 0, 0, false)]
    [InlineData(UsnDrainOutcome.NoSubscription, 0, 0, 0, 0, false)]
    public void OnlyARoundThatChangedNothing_CountsAsQuiet(
        UsnDrainOutcome outcome,
        int entries,
        int events,
        int gaps,
        int faulted,
        bool quiet)
    {
        var result = new UsnDrainResult(outcome, 1, faulted, entries, events, gaps);

        Assert.Equal(quiet, ChangeFeedDrainWorker.IsQuietRound(result));
    }

    [Fact]
    public void ARoundThatChangedNothing_StaysBelowTheEventLog()
    {
        var logger = new CaptureLogger();
        var worker = new ChangeFeedDrainWorker(logger);

        worker.LogRound(
            "S-1-5-18",
            new UsnDrainResult(UsnDrainOutcome.Completed, 1, 0, 0, 0, 0));

        Assert.Equal(LogLevel.Debug, Assert.Single(logger.Levels));
    }

    [Fact]
    public void ARoundThatQueuedEvents_ReachesTheEventLog()
    {
        var logger = new CaptureLogger();
        var worker = new ChangeFeedDrainWorker(logger);

        worker.LogRound(
            "S-1-5-18",
            new UsnDrainResult(UsnDrainOutcome.Completed, 1, 0, 1, 30, 0));

        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
    }

    [Fact]
    public void ARoundThatWroteAPageWithoutEvents_StillReachesTheEventLog()
    {
        var logger = new CaptureLogger();
        var worker = new ChangeFeedDrainWorker(logger);

        worker.LogRound(
            "S-1-5-18",
            new UsnDrainResult(UsnDrainOutcome.Completed, 1, 0, 1, 0, 0));

        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
    }

    [Fact]
    public void ARoundThatFoundAGap_ReachesTheEventLog()
    {
        var logger = new CaptureLogger();
        var worker = new ChangeFeedDrainWorker(logger);

        worker.LogRound(
            "S-1-5-18",
            new UsnDrainResult(UsnDrainOutcome.Completed, 1, 0, 0, 0, 1));

        Assert.Equal(LogLevel.Information, Assert.Single(logger.Levels));
    }

    [Fact]
    public void TheEventLogFilter_LetsInformationThrough()
    {
        var services = new ServiceCollection();
        services.AddLogging();
        services.AddChangeFeedService();

        using var provider = services.BuildServiceProvider();
        var options = provider.GetRequiredService<IOptions<LoggerFilterOptions>>().Value;

        var name = typeof(EventLogLoggerProvider).FullName;
        var rule = Assert.Single(options.Rules, candidate => candidate.ProviderName == name);

        Assert.NotNull(rule.Filter);
        Assert.False(rule.Filter!(name, null, LogLevel.Debug));
        Assert.True(rule.Filter!(name, null, LogLevel.Information));
        Assert.True(rule.Filter!(name, null, LogLevel.Warning));
    }

    private sealed class CaptureLogger : ILogger<ChangeFeedDrainWorker>
    {
        public List<LogLevel> Levels { get; } = new();

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(
            LogLevel logLevel,
            EventId eventId,
            TState state,
            Exception? exception,
            Func<TState, Exception?, string> formatter) => Levels.Add(logLevel);
    }

    [Theory]
    [InlineData(UsnDrainOutcome.LeaseHeld, true)]
    [InlineData(UsnDrainOutcome.LeasePreempted, true)]
    [InlineData(UsnDrainOutcome.Completed, false)]
    [InlineData(UsnDrainOutcome.NoSubscription, false)]
    [InlineData(UsnDrainOutcome.SubscriptionRejected, false)]
    [InlineData(UsnDrainOutcome.Faulted, false)]
    public void EveryLeaseOutcome_CountsAsPassive(UsnDrainOutcome outcome, bool passive)
    {
        Assert.Equal(passive, ChangeFeedDrainWorker.IsPassive(outcome));
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
