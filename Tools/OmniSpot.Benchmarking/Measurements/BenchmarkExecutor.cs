using System.Diagnostics;
using BenchmarkDotNet.Configs;
using BenchmarkDotNet.Diagnosers;
using BenchmarkDotNet.Engines;
using BenchmarkDotNet.Jobs;
using BenchmarkDotNet.Reports;
using BenchmarkDotNet.Running;
using Perfolizer.Mathematics.OutlierDetection;

namespace OmniSpot.Benchmarking.Measurements;

internal sealed record BenchmarkExecutionRequest(
    SearchFixtureManifest Fixture,
    int LaunchCount,
    int WarmupCount,
    int IterationCount,
    bool MemoryDiagnoserEnabled,
    double CanaryPercent,
    long CanaryDelayNanoseconds,
    string ArtifactsPath);

internal sealed record BenchmarkExecutionResult(
    IReadOnlyList<double> WarmupNanoseconds,
    IReadOnlyList<double> WorkloadNanoseconds,
    IReadOnlyList<double> OverheadNanoseconds,
    double? AllocatedBytesPerOperation,
    long WallMilliseconds);

internal static class BenchmarkExecutor
{
    internal static BenchmarkExecutionResult Run(BenchmarkExecutionRequest request)
    {
        Validate(request);
        var job = Job.Default
            .WithId("omnispot-search-state-create")
            .WithLaunchCount(request.LaunchCount)
            .WithWarmupCount(request.WarmupCount)
            .WithIterationCount(request.IterationCount)
            .WithInvocationCount(1)
            .WithUnrollFactor(1)
            .WithGcServer(false)
            .WithGcConcurrent(true)
            .DontEnforcePowerPlan()
            .WithOutlierMode(OutlierMode.DontRemove)
            .WithEnvironmentVariable(
                "OMNISPOT_BENCH_ITEM_COUNT",
                request.Fixture.ItemCount.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironmentVariable(
                "OMNISPOT_BENCH_SEED",
                request.Fixture.Seed.ToString(System.Globalization.CultureInfo.InvariantCulture))
            .WithEnvironmentVariable(
                "OMNISPOT_BENCH_FIXTURE_FINGERPRINT",
                request.Fixture.Fingerprint)
            .WithEnvironmentVariable(
                "OMNISPOT_BENCH_CANARY_DELAY_NS",
                request.CanaryDelayNanoseconds.ToString(
                    System.Globalization.CultureInfo.InvariantCulture));

        var config = ManualConfig.Create(DefaultConfig.Instance);
        config.ArtifactsPath = Path.GetFullPath(request.ArtifactsPath);
        config.AddJob(job);
        if (request.MemoryDiagnoserEnabled)
        {
            config.AddDiagnoser(MemoryDiagnoser.Default);
        }

        var stopwatch = Stopwatch.StartNew();
        var summary = BenchmarkRunner.Run<SearchStateCreateBenchmark>(config);
        stopwatch.Stop();
        var report = summary.Reports.SingleOrDefault()
            ?? throw new InvalidOperationException("BenchmarkDotNet ölçüm raporu üretmedi.");
        if (!report.Success)
        {
            throw new InvalidOperationException("BenchmarkDotNet ölçümü başarısız oldu.");
        }

        var warmup = SelectMeasurements(report, IterationStage.Warmup);
        var workload = SelectMeasurements(report, IterationStage.Result);
        var overhead = report.AllMeasurements
            .Where(measurement =>
                measurement.IterationMode == IterationMode.Overhead &&
                measurement.IterationStage == IterationStage.Actual)
            .Select(ToNanosecondsPerOperation)
            .ToArray();
        if (workload.Length == 0)
        {
            throw new InvalidOperationException("BenchmarkDotNet workload örneği üretmedi.");
        }

        double? allocatedBytes = request.MemoryDiagnoserEnabled
            ? report.GcStats.GetBytesAllocatedPerOperation(report.BenchmarkCase)
            : null;
        return new BenchmarkExecutionResult(
            warmup,
            workload,
            overhead,
            allocatedBytes,
            stopwatch.ElapsedMilliseconds);
    }

    private static double[] SelectMeasurements(
        BenchmarkReport report,
        IterationStage stage) =>
        report.AllMeasurements
            .Where(measurement =>
                measurement.IterationMode == IterationMode.Workload &&
                measurement.IterationStage == stage)
            .Select(ToNanosecondsPerOperation)
            .ToArray();

    private static double ToNanosecondsPerOperation(Measurement measurement) =>
        measurement.Nanoseconds / measurement.Operations;

    private static void Validate(BenchmarkExecutionRequest request)
    {
        if (request.WarmupCount < 1 || request.IterationCount < 1 || request.LaunchCount < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }

        if (request.CanaryPercent < 0 || request.CanaryDelayNanoseconds < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(request));
        }
    }
}
