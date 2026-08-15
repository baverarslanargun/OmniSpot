using OmniSpot.Benchmarking.Measurements;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class MeasurementStatisticsTests
{
    [Fact]
    public void P95_UsesNearestRank()
    {
        var values = Enumerable.Range(1, 20).Select(value => (double)value).ToArray();

        var result = MeasurementStatistics.P95(values);

        Assert.Equal(19, result);
    }
}
