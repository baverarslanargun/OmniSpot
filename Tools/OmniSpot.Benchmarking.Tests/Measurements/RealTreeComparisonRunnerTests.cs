using OmniSpot.Benchmarking.Measurements;
using OmniSpot.Benchmarking.Profiling;
using OmniSpot.Benchmarking.Tests.Profiling;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class RealTreeComparisonRunnerTests
{
    private const int ItemCount = 2_000;
    private const int Seed = 1701;

    // legacy replikası, builder değişikliğinden önceki SearchState.Create
    // gövdesini birebir taklit etmelidir. Etmiyorsa gerçek ağaç turundaki
    // "önce" tarafı yanlış olur ve karşılaştırma anlamını yitirir.
    [Fact]
    public void Run_LegacyReplicaProducesSameSearchStateAsProduction()
    {
        var comparison = RunComparison();

        Assert.Empty(comparison.AcceptanceFailures);
        Assert.Equal(ItemCount, comparison.TreeFacts.DistinctItemCount);
        Assert.Equal(ItemCount, comparison.TreeFacts.NodeCount);
    }

    [Fact]
    public void Run_ProducesPairedSamplesForBothVariants()
    {
        var comparison = RunComparison();

        Assert.Equal(4, comparison.Samples.Count);
        Assert.Equal(2, comparison.Samples.Count(sample => sample.Variant == "legacy"));
        Assert.Equal(2, comparison.Samples.Count(sample => sample.Variant == "builder"));
        Assert.Equal(2, comparison.Variants.Count);
    }

    [Fact]
    public void Run_CarriesSchemaVersionAndEnvironmentManifest()
    {
        var comparison = RunComparison();

        Assert.True(comparison.SchemaMajor > 0);
        Assert.True(comparison.SchemaMinor >= 0);
        Assert.NotNull(comparison.Environment);
        Assert.Equal("test-os", comparison.Environment.OsDescription);
    }

    // Kapı yalnız allocation düşüşü barajı geçilirse ve doğruluk kapısı
    // temizse geçmiş sayılır.
    [Fact]
    public void Run_DoesNotClaimBarWhenReductionIsBelowThreshold()
    {
        var comparison = RunComparison(allocationBarPercent: 99.9);

        Assert.False(comparison.MeetsAllocationBar);
        Assert.True(comparison.AllocationChangePercent < 0);
    }

    // Sözleşme §8.1: koşum içinde frekans veya PROCTHROTTLEMAX kayması varsa
    // sonuç kalıcı baseline olamaz; kabul kapısı düşmeli.
    [Fact]
    public void Run_FailsAcceptanceWhenFrequencyDriftIsLabelled()
    {
        var comparison = RunComparison(labels: ["frekans-kaymasi"]);

        Assert.False(comparison.MeetsAllocationBar);
        Assert.Contains(
            comparison.AcceptanceFailures,
            failure => failure.Contains("frekans-kaymasi", StringComparison.Ordinal));
    }

    private static RealTreeComparison RunComparison(
        double allocationBarPercent = 50,
        IReadOnlyList<string>? labels = null) =>
        RealTreeComparisonRunner.Run(
            SyntheticSearchFixtureGenerator.Create(ItemCount, Seed).Nodes,
            rounds: 1,
            allocationBarPercent,
            enumerationMilliseconds: 0,
            TimeSpan.FromMinutes(2),
            ProfileEnvironmentCapture.FromCompleted(
                ProfileTestFixture.CreateEnvironment(labels)),
            CancellationToken.None);
}
