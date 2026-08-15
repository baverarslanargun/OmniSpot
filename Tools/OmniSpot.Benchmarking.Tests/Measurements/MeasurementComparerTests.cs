using OmniSpot.Benchmarking.Measurements;
using OmniSpot.Benchmarking.Profiling;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class MeasurementComparerTests
{
    [Fact]
    public void Compare_SingleCompletedThresholdExceedanceIsRegression()
    {
        var fixture = SyntheticSearchFixtureGenerator.Create(100, 1701).Manifest;
        var baseline = MeasurementTestData.CreateDocument(fixture, p95Nanoseconds: 100);
        var candidate = MeasurementTestData.CreateDocument(fixture, p95Nanoseconds: 106);

        var comparison = MeasurementComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.Regression, comparison.Verdict);
        Assert.Equal(6, comparison.P95ChangePercent, precision: 6);
        Assert.Contains("processor_frequency_compatible", comparison.Guards);
    }

    [Fact]
    public void Compare_ChangeBelowThresholdIsNotCalledUnchanged()
    {
        var fixture = SyntheticSearchFixtureGenerator.Create(100, 1701).Manifest;
        var baseline = MeasurementTestData.CreateDocument(fixture, p95Nanoseconds: 100);
        var candidate = MeasurementTestData.CreateDocument(fixture, p95Nanoseconds: 101);

        var comparison = MeasurementComparer.Compare(baseline, candidate);

        Assert.Equal(ComparisonVerdict.UnmeasurableWithinBudget, comparison.Verdict);
    }

    [Fact]
    public void Compare_RejectsDifferentFixtureFingerprint()
    {
        var baselineFixture = SyntheticSearchFixtureGenerator.Create(100, 1701).Manifest;
        var candidateFixture = SyntheticSearchFixtureGenerator.Create(100, 1702).Manifest;

        var exception = Assert.Throws<IncompatibleMeasurementException>(() =>
            MeasurementComparer.Compare(
                MeasurementTestData.CreateDocument(baselineFixture, 100),
                MeasurementTestData.CreateDocument(candidateFixture, 100)));

        Assert.Contains("fixture_fingerprint_mismatch", exception.Failures);
    }

    [Fact]
    public void Compare_RejectsDifferentEnvironment()
    {
        var fixture = SyntheticSearchFixtureGenerator.Create(100, 1701).Manifest;
        var baseline = MeasurementTestData.CreateDocument(fixture, 100);
        var candidate = MeasurementTestData.CreateDocument(
            fixture,
            100,
            MeasurementTestData.Environment with { DefenderRealtimeEnabled = false });

        var exception = Assert.Throws<IncompatibleMeasurementException>(() =>
            MeasurementComparer.Compare(baseline, candidate));

        Assert.Contains("environment_defender_mismatch", exception.Failures);
    }

    [Fact]
    public void Compare_RejectsDifferentProcessorFrequencyPolicy()
    {
        var fixture = SyntheticSearchFixtureGenerator.Create(100, 1701).Manifest;
        var baseline = MeasurementTestData.CreateDocument(fixture, 100);
        var candidate = MeasurementTestData.CreateDocument(
            fixture,
            100,
            MeasurementTestData.Environment with
            {
                ProcessorThrottleMaxAcStartPercent = 100,
                ProcessorThrottleMaxAcEndPercent = 100
            });

        var exception = Assert.Throws<IncompatibleMeasurementException>(() =>
            MeasurementComparer.Compare(baseline, candidate));

        Assert.Contains("environment_frequency_mismatch", exception.Failures);
    }

    [Fact]
    public void Compare_RejectsFrequencyDriftLabel()
    {
        var fixture = SyntheticSearchFixtureGenerator.Create(100, 1701).Manifest;
        var baseline = MeasurementTestData.CreateDocument(fixture, 100);
        var candidate = MeasurementTestData.CreateDocument(
            fixture,
            100,
            MeasurementTestData.Environment with
            {
                ProcessorFrequencyEndMhz = 3250,
                ProcessorFrequencyDriftPercent = 4.17,
                Labels = ["frekans-kaymasi"]
            });

        var exception = Assert.Throws<IncompatibleMeasurementException>(() =>
            MeasurementComparer.Compare(baseline, candidate));

        Assert.Contains("environment_frequency_mismatch", exception.Failures);
    }

    [Fact]
    public void Compare_RejectsDifferentContractOrToolVersion()
    {
        var fixture = SyntheticSearchFixtureGenerator.Create(100, 1701).Manifest;
        var baseline = MeasurementTestData.CreateDocument(fixture, 100);
        var candidate = MeasurementTestData.CreateDocument(fixture, 100) with
        {
            ContractVersion = "2.0",
            ToolVersion = "9.9.9"
        };

        var exception = Assert.Throws<IncompatibleMeasurementException>(() =>
            MeasurementComparer.Compare(baseline, candidate));

        Assert.Contains("contract_version_mismatch", exception.Failures);
        Assert.Contains("tool_version_mismatch", exception.Failures);
    }
}

internal static class MeasurementTestData
{
    internal static ProfileEnvironment Environment { get; } = new(
        "test-os",
        "test-framework",
        "8.0.100",
        "x64",
        "test-cpu",
        8,
        16L * 1024 * 1024 * 1024,
        ServerGc: false,
        "Interactive",
        OmniSpotProcessRunning: false,
        RepoHead: "0000000000000000000000000000000000000000",
        RepoDirty: false,
        RepoDirtyEntryCount: 0,
        PowerPlanGuid: "00000000-0000-0000-0000-000000000001",
        DefenderRealtimeEnabled: true,
        WindowsSearchRunning: true,
        DiskKind: null,
        ProcessorThrottleMaxAcStartPercent: 99,
        ProcessorThrottleMaxDcStartPercent: 99,
        ProcessorThrottleMaxAcEndPercent: 99,
        ProcessorThrottleMaxDcEndPercent: 99,
        ProcessorNominalBaseMhz: 3300,
        ProcessorFrequencyStartMhz: 3120,
        ProcessorFrequencyEndMhz: 3130,
        ProcessorFrequencyDriftPercent: 0.32,
        Labels: Array.Empty<string>());

    internal static MeasurementDocument CreateDocument(
        SearchFixtureManifest fixture,
        double p95Nanoseconds,
        ProfileEnvironment? environment = null) =>
        new(
            MeasurementConstants.SchemaMajor,
            MeasurementConstants.SchemaMinor,
            MeasurementConstants.ContractVersion,
            MeasurementConstants.ToolVersion,
            MeasurementConstants.MetricName,
            StartedUnixSeconds: 1,
            CompletedUnixSeconds: 2,
            fixture,
            environment ?? Environment,
            new MeasurementConfiguration(
                WarmupCount: 6,
                IterationCount: 30,
                MemoryDiagnoserEnabled: true,
                ServerGc: false,
                ConcurrentGc: true,
                MeasurementConstants.OutlierMode,
                MeasurementConstants.PercentileMethod,
                SteadyMemoryIdleSeconds: 30,
                RegressionThresholdPercent: 5,
                MinimumDetectableDifferencePercent: 2,
                CanaryPercent: 0,
                CanaryDelayNanoseconds: 0),
            new MeasurementMetrics(
                WarmupNanoseconds: [90, 95, 100],
                WorkloadNanoseconds: [p95Nanoseconds],
                OverheadNanoseconds: [1],
                MedianNanoseconds: p95Nanoseconds,
                P95Nanoseconds: p95Nanoseconds,
                AllocatedBytesPerOperation: 1_000,
                new InstrumentationOverhead(1, 1, 1, 100),
                SteadyMemory: []));
}
