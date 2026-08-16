using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using OmniSpot.Benchmarking.Profiling;

namespace OmniSpot.Benchmarking.Measurements;

internal static class MeasurementConstants
{
    internal const int SchemaMajor = 1;
    internal const int SchemaMinor = 1;
    internal const string ContractVersion = "0.1";
    internal const string ToolVersion = "0.2.0";
    internal const string FixtureGeneratorVersion = "1.0";
    internal const string MetricName = "publish.create.public";
    internal const string PercentileMethod = "nearest_rank";
    internal const string OutlierMode = "dont_remove";
    internal const int DefaultSeed = 1701;
    internal static readonly int FrozenItemCount = 0;
    internal static readonly int FrozenLaunchCount = 0;
    internal static readonly int FrozenWarmupCount = 0;
    internal static readonly int FrozenMeasurementCount = 0;
    internal static readonly int FrozenIdleSeconds = 0;
    internal static readonly double FrozenRegressionThresholdPercent = 0;
    internal static readonly double FrozenMinimumDetectableDifferencePercent = 0;
}

internal sealed record SearchFixtureManifest(
    string GeneratorVersion,
    int Seed,
    int ItemCount,
    string Fingerprint);

internal sealed record MeasurementConfiguration(
    int WarmupCount,
    int IterationCount,
    bool MemoryDiagnoserEnabled,
    bool ServerGc,
    bool ConcurrentGc,
    string OutlierMode,
    string PercentileMethod,
    int SteadyMemoryIdleSeconds,
    double RegressionThresholdPercent,
    double MinimumDetectableDifferencePercent,
    double CanaryPercent,
    long CanaryDelayNanoseconds);

internal sealed record SteadyMemorySample(
    int IdleSeconds,
    long ManagedMemoryBytes,
    long PrivateMemoryBytes);

internal sealed record InstrumentationOverhead(
    double StopwatchTimestampPairNanoseconds,
    double AllocationCounterPairNanoseconds,
    double BenchmarkOverheadMedianNanoseconds,
    long BenchmarkWallMilliseconds);

internal sealed record MeasurementMetrics(
    IReadOnlyList<double> WarmupNanoseconds,
    IReadOnlyList<double> WorkloadNanoseconds,
    IReadOnlyList<double> OverheadNanoseconds,
    double MedianNanoseconds,
    double P95Nanoseconds,
    double? AllocatedBytesPerOperation,
    InstrumentationOverhead Instrumentation,
    IReadOnlyList<SteadyMemorySample> SteadyMemory);

internal sealed record MeasurementDocument(
    int SchemaMajor,
    int SchemaMinor,
    string ContractVersion,
    string ToolVersion,
    string Metric,
    long StartedUnixSeconds,
    long CompletedUnixSeconds,
    SearchFixtureManifest Fixture,
    ProfileEnvironment Environment,
    MeasurementConfiguration Configuration,
    MeasurementMetrics Metrics);

internal enum ComparisonVerdict
{
    Regression,
    Improvement,
    UnmeasurableWithinBudget
}

internal sealed record ComparisonDocument(
    int SchemaMajor,
    int SchemaMinor,
    string ContractVersion,
    string ToolVersion,
    long ComparedUnixSeconds,
    ComparisonVerdict Verdict,
    double P95ChangePercent,
    double? AllocationChangePercent,
    double RegressionThresholdPercent,
    double MinimumDetectableDifferencePercent,
    IReadOnlyList<string> Guards);

internal sealed record CalibrationSample(
    int ItemCount,
    double ElapsedMilliseconds);

internal sealed record PilotRegime(
    bool Passed,
    int ItemCount,
    int WarmupCount,
    int MeasurementCount,
    string PercentileMethod,
    string OutlierMode,
    double TwoRunVarianceBandPercent,
    double RegressionThresholdPercent,
    double? MinimumDetectableDifferencePercent,
    int SteadyMemoryIdleSeconds,
    double MemoryDiagnoserWallOverheadPercent,
    double ProjectedDailyComparisonMinutes,
    string DecisionStatistic,
    string CanaryDesign);

/// <summary>
/// Bir canary basamağının eşleştirilmiş sonucu. Referans, canary koşumunun hemen
/// öncesindeki ve sonrasındaki baseline koşumlarının ortalamasıdır; bu sayede
/// zaman eksenindeki doğrusal drift birinci derecede iptal olur.
/// </summary>
internal sealed record CanaryPairResult(
    double CanaryPercent,
    string CanaryLabel,
    string BaselineBeforeLabel,
    string BaselineAfterLabel,
    double ReferenceMedianNanoseconds,
    double CanaryMedianNanoseconds,
    double PairedChangePercent,
    bool Detected);

internal sealed record PilotRun(
    string Label,
    double CanaryPercent,
    long WallMilliseconds,
    double MedianNanoseconds,
    double P95Nanoseconds,
    double? AllocatedBytesPerOperation,
    IReadOnlyList<double> WarmupNanoseconds,
    IReadOnlyList<double> WorkloadNanoseconds,
    IReadOnlyList<double> OverheadNanoseconds);

internal sealed record PilotDocument(
    int SchemaMajor,
    int SchemaMinor,
    string ContractVersion,
    string ToolVersion,
    long StartedUnixSeconds,
    long CompletedUnixSeconds,
    SearchFixtureManifest Fixture,
    ProfileEnvironment Environment,
    IReadOnlyList<CalibrationSample> Calibration,
    InstrumentationOverhead Instrumentation,
    IReadOnlyList<SteadyMemorySample> SteadyMemory,
    IReadOnlyList<PilotRun> Runs,
    IReadOnlyList<CanaryPairResult> CanaryPairs,
    PilotRegime Regime,
    IReadOnlyList<string> AcceptanceFailures);

internal static class MeasurementJson
{
    internal static JsonSerializerOptions Options { get; } = CreateOptions();

    internal static string Serialize<T>(T value) =>
        JsonSerializer.Serialize(value, Options);

    internal static T Deserialize<T>(string json) =>
        JsonSerializer.Deserialize<T>(json, Options)
        ?? throw new InvalidDataException("Benchmark JSON belgesi çözümlenemedi.");

    internal static async Task WriteAsync<T>(
        string path,
        T value,
        CancellationToken cancellationToken)
    {
        var fullPath = Path.GetFullPath(path);
        var directory = Path.GetDirectoryName(fullPath);
        if (!string.IsNullOrWhiteSpace(directory))
        {
            Directory.CreateDirectory(directory);
        }

        await File.WriteAllTextAsync(
            fullPath,
            Serialize(value) + Environment.NewLine,
            new UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
            cancellationToken);
    }

    private static JsonSerializerOptions CreateOptions()
    {
        var options = new JsonSerializerOptions
        {
            PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
            WriteIndented = true
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.SnakeCaseLower));
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}
