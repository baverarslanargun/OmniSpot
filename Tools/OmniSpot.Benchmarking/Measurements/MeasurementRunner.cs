using OmniSpot.Benchmarking.Profiling;

namespace OmniSpot.Benchmarking.Measurements;

internal sealed record MeasurementRunRequest(
    int ItemCount,
    int Seed,
    int LaunchCount,
    int WarmupCount,
    int IterationCount,
    bool MemoryDiagnoserEnabled,
    IReadOnlyList<int> SteadyMemoryIdleSeconds,
    double RegressionThresholdPercent,
    double MinimumDetectableDifferencePercent,
    double CanaryPercent,
    long CanaryDelayNanoseconds,
    string ArtifactsPath);

internal static class MeasurementRunner
{
    internal static MeasurementDocument Run(
        MeasurementRunRequest request,
        ProfileEnvironmentCapture environmentCapture,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(environmentCapture);
        var started = DateTimeOffset.UtcNow;
        var fixture = SyntheticSearchFixtureGenerator.Create(request.ItemCount, request.Seed);
        var instrument = InstrumentationProbe.Measure();
        cancellationToken.ThrowIfCancellationRequested();
        var benchmark = BenchmarkExecutor.Run(new BenchmarkExecutionRequest(
            fixture.Manifest,
            request.LaunchCount,
            request.WarmupCount,
            request.IterationCount,
            request.MemoryDiagnoserEnabled,
            request.CanaryPercent,
            request.CanaryDelayNanoseconds,
            request.ArtifactsPath));
        cancellationToken.ThrowIfCancellationRequested();
        var steadyMemory = SteadyMemoryProbe.Run(
            fixture,
            request.SteadyMemoryIdleSeconds,
            cancellationToken);
        var environment = environmentCapture.Complete();
        var completed = DateTimeOffset.UtcNow;
        var configuration = new MeasurementConfiguration(
            request.WarmupCount,
            request.IterationCount,
            request.MemoryDiagnoserEnabled,
            ServerGc: false,
            ConcurrentGc: true,
            MeasurementConstants.OutlierMode,
            MeasurementConstants.PercentileMethod,
            request.SteadyMemoryIdleSeconds.Count == 0
                ? 0
                : request.SteadyMemoryIdleSeconds.Max(),
            request.RegressionThresholdPercent,
            request.MinimumDetectableDifferencePercent,
            request.CanaryPercent,
            request.CanaryDelayNanoseconds);
        var metrics = new MeasurementMetrics(
            benchmark.WarmupNanoseconds,
            benchmark.WorkloadNanoseconds,
            benchmark.OverheadNanoseconds,
            MeasurementStatistics.Median(benchmark.WorkloadNanoseconds),
            MeasurementStatistics.P95(benchmark.WorkloadNanoseconds),
            benchmark.AllocatedBytesPerOperation,
            new InstrumentationOverhead(
                instrument.TimestampPairNanoseconds,
                instrument.AllocationPairNanoseconds,
                benchmark.OverheadNanoseconds.Count == 0
                    ? 0
                    : MeasurementStatistics.Median(benchmark.OverheadNanoseconds),
                benchmark.WallMilliseconds),
            steadyMemory);
        return new MeasurementDocument(
            MeasurementConstants.SchemaMajor,
            MeasurementConstants.SchemaMinor,
            MeasurementConstants.ContractVersion,
            MeasurementConstants.ToolVersion,
            MeasurementConstants.MetricName,
            started.ToUnixTimeSeconds(),
            completed.ToUnixTimeSeconds(),
            fixture.Manifest,
            environment,
            configuration,
            metrics);
    }
}
