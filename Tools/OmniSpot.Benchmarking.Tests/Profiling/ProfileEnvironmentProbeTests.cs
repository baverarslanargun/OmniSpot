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
}
