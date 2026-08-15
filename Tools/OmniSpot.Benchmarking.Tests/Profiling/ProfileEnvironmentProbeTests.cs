using OmniSpot.Benchmarking.Profiling;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Profiling;

public sealed class ProfileEnvironmentProbeTests
{
    [Theory]
    [InlineData("1", true)]
    [InlineData(" 0 \r\n", false)]
    public void ParseBooleanProbe_RecognizesNumericPowerShellOutput(
        string output,
        bool expected)
    {
        Assert.Equal(expected, ProfileEnvironmentProbe.ParseBooleanProbe(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("True")]
    [InlineData("2")]
    public void ParseBooleanProbe_ReturnsNullForUnknownOutput(string output)
    {
        Assert.Null(ProfileEnvironmentProbe.ParseBooleanProbe(output));
    }

    [Theory]
    [InlineData("3", "hdd")]
    [InlineData("SSD\r\n4\n", "ssd")]
    [InlineData("5", "scm")]
    [InlineData("HDD\n4", "mixed")]
    public void ParseDiskKind_NormalizesKnownMediaTypes(string output, string expected)
    {
        Assert.Equal(expected, ProfileEnvironmentProbe.ParseDiskKind(output));
    }

    [Theory]
    [InlineData("")]
    [InlineData("0")]
    [InlineData("4\nunknown")]
    public void ParseDiskKind_ReturnsNullForIncompleteOrUnknownOutput(string output)
    {
        Assert.Null(ProfileEnvironmentProbe.ParseDiskKind(output));
    }

    [Fact]
    public void CountRepositoryStatusEntries_IncludesTrackedAndUntrackedLines()
    {
        const string status = " M docs/README.md\r\n?? Tools/Profiler/\r\n?? docs/contract.md\r\n";

        Assert.Equal(3, ProfileEnvironmentProbe.CountRepositoryStatusEntries(status));
    }

    [Theory]
    [InlineData(3120, 3130, 0.3205128205)]
    [InlineData(3120, 3300, 5.7692307692)]
    public void CalculateDriftPercent_UsesStartFrequencyAsDenominator(
        int startMhz,
        int endMhz,
        double expected)
    {
        var actual = ProcessorFrequencyProbe.CalculateDriftPercent(startMhz, endMhz);

        Assert.NotNull(actual);
        Assert.Equal(expected, actual.Value, precision: 8);
    }

    [Fact]
    public void IsDrift_UsesStrictTwoPercentBoundary()
    {
        Assert.False(ProcessorFrequencyProbe.IsDrift(2));
        Assert.True(ProcessorFrequencyProbe.IsDrift(2.01));
    }

    [Fact]
    public void EnvironmentCapture_CompletesOnlyOnce()
    {
        var completionCount = 0;
        var environment = CreateEnvironment();
        var capture = new ProfileEnvironmentCapture(
            environment,
            () =>
            {
                completionCount++;
                return environment;
            });

        Assert.Same(environment, capture.Complete());
        Assert.Same(environment, capture.Complete());
        Assert.Equal(1, completionCount);
    }

    private static ProfileEnvironment CreateEnvironment() => new(
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
        RepoHead: null,
        RepoDirty: null,
        RepoDirtyEntryCount: null,
        PowerPlanGuid: null,
        DefenderRealtimeEnabled: null,
        WindowsSearchRunning: null,
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
}
