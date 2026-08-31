using OmniSpot.Benchmarking.Measurements;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class PilotRunnerTests
{
    private const double DriftPerRunNanoseconds = 15;
    private const double CanaryFactor = 1.08;

    [Fact]
    public void PairedReference_KeepsCanarySignalUnderLinearDrift()
    {
        var baselineBefore = 485d;
        var canaryTrueBaseline = baselineBefore - DriftPerRunNanoseconds;
        var canaryMeasured = canaryTrueBaseline * CanaryFactor;
        var baselineAfter = canaryTrueBaseline - DriftPerRunNanoseconds;

        var reference = PilotRunner.PairedReference(baselineBefore, baselineAfter);
        var pairedChange = MeasurementStatistics.ChangePercent(reference, canaryMeasured);

        Assert.Equal(canaryTrueBaseline, reference, 6);
        Assert.Equal(8, pairedChange, 6);
    }

    [Fact]
    public void SequentialComparison_LosesTheSameCanarySignal()
    {
        var firstBaseline = 500d;
        var baselineBefore = firstBaseline - DriftPerRunNanoseconds;
        var canaryTrueBaseline = baselineBefore - DriftPerRunNanoseconds;
        var canaryMeasured = canaryTrueBaseline * CanaryFactor;
        var baselineAfter = canaryTrueBaseline - DriftPerRunNanoseconds;

        var sequentialChange = MeasurementStatistics.ChangePercent(
            firstBaseline,
            canaryMeasured);
        var pairedChange = MeasurementStatistics.ChangePercent(
            PilotRunner.PairedReference(baselineBefore, baselineAfter),
            canaryMeasured);

        Assert.Equal(8, pairedChange, 6);
        Assert.True(
            sequentialChange < pairedChange,
            "Sıralı karşılaştırma drift altında sinyali küçültmelidir.");
        Assert.True(
            sequentialChange < 3,
            "Bu drift büyüklüğünde sıralı karşılaştırma %8 canary'yi %3'ün altına düşürür.");
    }

    [Fact]
    public void PairedReference_IsExactWhenThereIsNoDrift()
    {
        var pairedChange = MeasurementStatistics.ChangePercent(
            PilotRunner.PairedReference(500, 500),
            500 * CanaryFactor);

        Assert.Equal(8, pairedChange, 6);
    }

    [Fact]
    public void VarianceBandPercent_UsesLargestConsecutiveGap()
    {
        double[] medians = [500, 505, 495, 496];

        var band = PilotRunner.VarianceBandPercent(medians);

        Assert.Equal(1.980198, band, 5);
    }

    [Fact]
    public void VarianceBandPercent_IsZeroForASingleBaseline()
    {
        Assert.Equal(0, PilotRunner.VarianceBandPercent([500]));
    }
}
