using OmniSpot.Benchmarking.Profiling;

namespace OmniSpot.Benchmarking.Measurements;

internal sealed class IncompatibleMeasurementException : Exception
{
    internal IncompatibleMeasurementException(IReadOnlyList<string> failures)
        : base("Benchmark sonuçları karşılaştırılabilir değil.")
    {
        Failures = failures;
    }

    internal IReadOnlyList<string> Failures { get; }
}

internal static class MeasurementComparer
{
    internal static ComparisonDocument Compare(
        MeasurementDocument baseline,
        MeasurementDocument candidate,
        bool allowCanaryDifference = false)
    {
        ArgumentNullException.ThrowIfNull(baseline);
        ArgumentNullException.ThrowIfNull(candidate);
        var failures = ValidateCompatibility(baseline, candidate, allowCanaryDifference);
        if (failures.Count > 0)
        {
            throw new IncompatibleMeasurementException(failures);
        }

        var p95Change = MeasurementStatistics.ChangePercent(
            baseline.Metrics.P95Nanoseconds,
            candidate.Metrics.P95Nanoseconds);
        double? allocationChange = null;
        if (baseline.Metrics.AllocatedBytesPerOperation is double baselineAllocation &&
            baselineAllocation > 0 &&
            candidate.Metrics.AllocatedBytesPerOperation is double candidateAllocation)
        {
            allocationChange = MeasurementStatistics.ChangePercent(
                baselineAllocation,
                candidateAllocation);
        }

        var threshold = baseline.Configuration.RegressionThresholdPercent;
        var regression = p95Change > threshold || allocationChange is > 0 && allocationChange > threshold;
        var improvement = !regression &&
            (p95Change < -threshold || allocationChange is < 0 && allocationChange < -threshold);
        var verdict = regression
            ? ComparisonVerdict.Regression
            : improvement
                ? ComparisonVerdict.Improvement
                : ComparisonVerdict.UnmeasurableWithinBudget;
        return new ComparisonDocument(
            baseline.SchemaMajor,
            baseline.SchemaMinor,
            baseline.ContractVersion,
            baseline.ToolVersion,
            DateTimeOffset.UtcNow.ToUnixTimeSeconds(),
            verdict,
            p95Change,
            allocationChange,
            threshold,
            baseline.Configuration.MinimumDetectableDifferencePercent,
            [
                "fixture_fingerprint_equal",
                "environment_compatible",
                "contract_and_tool_version_equal",
                "processor_frequency_compatible"
            ]);
    }

    internal static IReadOnlyList<string> ValidateCompatibility(
        MeasurementDocument baseline,
        MeasurementDocument candidate,
        bool allowCanaryDifference)
    {
        var failures = new List<string>();
        Require(
            baseline.SchemaMajor == candidate.SchemaMajor &&
            baseline.SchemaMinor == candidate.SchemaMinor,
            "schema_version_mismatch",
            failures);
        Require(
            string.Equals(baseline.ContractVersion, candidate.ContractVersion, StringComparison.Ordinal),
            "contract_version_mismatch",
            failures);
        Require(
            string.Equals(baseline.ToolVersion, candidate.ToolVersion, StringComparison.Ordinal),
            "tool_version_mismatch",
            failures);
        Require(
            string.Equals(baseline.Metric, candidate.Metric, StringComparison.Ordinal),
            "metric_mismatch",
            failures);
        Require(
            baseline.Fixture == candidate.Fixture,
            "fixture_fingerprint_mismatch",
            failures);
        Require(
            ConfigurationsMatch(
                baseline.Configuration,
                candidate.Configuration,
                allowCanaryDifference),
            "measurement_configuration_mismatch",
            failures);
        foreach (var failure in CompareEnvironment(baseline.Environment, candidate.Environment))
        {
            failures.Add(failure);
        }

        Require(
            FrequencyEnvironmentsMatch(baseline.Environment, candidate.Environment),
            "environment_frequency_mismatch",
            failures);

        Require(
            baseline.Configuration.RegressionThresholdPercent > 0 &&
            baseline.Configuration.MinimumDetectableDifferencePercent > 0,
            "measurement_regime_not_frozen",
            failures);
        return failures;
    }

    private static bool ConfigurationsMatch(
        MeasurementConfiguration baseline,
        MeasurementConfiguration candidate,
        bool allowCanaryDifference)
    {
        if (!allowCanaryDifference)
        {
            return baseline == candidate;
        }

        return baseline with
        {
            CanaryPercent = candidate.CanaryPercent,
            CanaryDelayNanoseconds = candidate.CanaryDelayNanoseconds
        } == candidate;
    }

    private static IReadOnlyList<string> CompareEnvironment(
        ProfileEnvironment baseline,
        ProfileEnvironment candidate)
    {
        var failures = new List<string>();
        Require(baseline.OsDescription == candidate.OsDescription, "environment_os_mismatch", failures);
        Require(baseline.FrameworkDescription == candidate.FrameworkDescription, "environment_framework_mismatch", failures);
        Require(baseline.DotnetSdkVersion == candidate.DotnetSdkVersion, "environment_sdk_mismatch", failures);
        Require(baseline.ProcessArchitecture == candidate.ProcessArchitecture, "environment_architecture_mismatch", failures);
        Require(baseline.ProcessorModel == candidate.ProcessorModel, "environment_processor_mismatch", failures);
        Require(baseline.LogicalProcessorCount == candidate.LogicalProcessorCount, "environment_cpu_count_mismatch", failures);
        Require(baseline.ServerGc == candidate.ServerGc, "environment_gc_mismatch", failures);
        Require(baseline.GcLatencyMode == candidate.GcLatencyMode, "environment_gc_latency_mismatch", failures);
        Require(baseline.PowerPlanGuid == candidate.PowerPlanGuid, "environment_power_plan_mismatch", failures);
        Require(baseline.DefenderRealtimeEnabled == candidate.DefenderRealtimeEnabled, "environment_defender_mismatch", failures);
        Require(baseline.WindowsSearchRunning == candidate.WindowsSearchRunning, "environment_windows_search_mismatch", failures);
        Require(baseline.OmniSpotProcessRunning == candidate.OmniSpotProcessRunning, "environment_omnispot_process_mismatch", failures);
        return failures;
    }

    private static bool FrequencyEnvironmentsMatch(
        ProfileEnvironment baseline,
        ProfileEnvironment candidate)
    {
        if (baseline.Labels.Contains("frekans-kaymasi", StringComparer.Ordinal) ||
            candidate.Labels.Contains("frekans-kaymasi", StringComparer.Ordinal) ||
            baseline.ProcessorThrottleMaxAcStartPercent is not int baselineAcStart ||
            baseline.ProcessorThrottleMaxAcEndPercent is not int baselineAcEnd ||
            baseline.ProcessorThrottleMaxDcStartPercent is not int baselineDcStart ||
            baseline.ProcessorThrottleMaxDcEndPercent is not int baselineDcEnd ||
            candidate.ProcessorThrottleMaxAcStartPercent is not int candidateAcStart ||
            candidate.ProcessorThrottleMaxAcEndPercent is not int candidateAcEnd ||
            candidate.ProcessorThrottleMaxDcStartPercent is not int candidateDcStart ||
            candidate.ProcessorThrottleMaxDcEndPercent is not int candidateDcEnd ||
            baseline.ProcessorNominalBaseMhz is not int baselineBase ||
            candidate.ProcessorNominalBaseMhz is not int candidateBase ||
            AverageLoadedMhz(baseline) is not double baselineLoaded ||
            AverageLoadedMhz(candidate) is not double candidateLoaded)
        {
            return false;
        }

        return baselineAcStart == baselineAcEnd &&
            baselineDcStart == baselineDcEnd &&
            candidateAcStart == candidateAcEnd &&
            candidateDcStart == candidateDcEnd &&
            baselineAcStart == candidateAcStart &&
            baselineDcStart == candidateDcStart &&
            baselineBase == candidateBase &&
            Math.Abs(MeasurementStatistics.ChangePercent(baselineLoaded, candidateLoaded)) <=
                ProcessorFrequencyProbe.DriftThresholdPercent;
    }

    private static double? AverageLoadedMhz(ProfileEnvironment environment)
    {
        if (environment.ProcessorFrequencyStartMhz is not > 0 ||
            environment.ProcessorFrequencyEndMhz is not > 0)
        {
            return null;
        }

        return (environment.ProcessorFrequencyStartMhz.Value +
            environment.ProcessorFrequencyEndMhz.Value) / 2d;
    }

    private static void Require(bool condition, string failure, ICollection<string> failures)
    {
        if (!condition)
        {
            failures.Add(failure);
        }
    }
}
