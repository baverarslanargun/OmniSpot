using OmniSpot.Benchmarking.Measurements;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class PhaseSplitRunnerTests
{
    private const int ItemCount = 2_000;
    private const int Seed = 1701;

    [Fact]
    public void Verify_ReplicaReproducesProductionSearchState()
    {
        var fixture = SyntheticSearchFixtureGenerator.Create(ItemCount, Seed);

        var (facts, failures) = PhaseSplitRunner.Verify(fixture.Nodes, new BasicTokenizer());

        Assert.Empty(failures);
        Assert.Equal(ItemCount, facts.DistinctItemCount);
        Assert.True(facts.UniqueTokenCount > 0);
        Assert.True(facts.TokenToItemLinkCount >= facts.UniqueTokenCount);
    }

    [Fact]
    public void Verify_CurrentFixtureHasNoParentLinkedItems()
    {
        var fixture = SyntheticSearchFixtureGenerator.Create(ItemCount, Seed);

        var (facts, _) = PhaseSplitRunner.Verify(fixture.Nodes, new BasicTokenizer());

        Assert.Equal(0, facts.ParentLinkedItemCount);
    }

    [Fact]
    public void BuildPhases_DeltasSumToProductionTotal()
    {
        var phases = PhaseSplitRunner.BuildPhases(
        [
            Stage("enumerate", 100, 1_000),
            Stage("distinct", 400, 5_000),
            Stage("tokens", 900, 9_000),
            Stage("token_sets", 1_600, 20_000),
            Stage("postings", 3_600, 60_000),
            Stage("production", 4_000, 80_000)
        ]);

        Assert.Equal(4_000, phases.Sum(phase => phase.Nanoseconds), 6);
        Assert.Equal(80_000, phases.Sum(phase => phase.AllocatedBytes), 6);
        Assert.Equal(100, phases.Sum(phase => phase.NanosecondSharePercent), 6);
        Assert.Equal(100, phases.Sum(phase => phase.AllocationSharePercent), 6);
    }

    [Fact]
    public void BuildPhases_AttributesEachDeltaToItsBoundary()
    {
        var phases = PhaseSplitRunner.BuildPhases(
        [
            Stage("enumerate", 100, 1_000),
            Stage("distinct", 400, 5_000),
            Stage("tokens", 900, 9_000),
            Stage("token_sets", 1_600, 20_000),
            Stage("postings", 3_600, 60_000),
            Stage("production", 4_000, 80_000)
        ]);

        Assert.Equal(300, Phase(phases, "distinct").Nanoseconds, 6);
        Assert.Equal(500, Phase(phases, "tokenize").Nanoseconds, 6);
        Assert.Equal(700, Phase(phases, "token_sets").Nanoseconds, 6);
        Assert.Equal(2_000, Phase(phases, "postings").Nanoseconds, 6);
        Assert.Equal(400, Phase(phases, "children_publish").Nanoseconds, 6);
        Assert.False(Phase(phases, "tokenize").CoveredByR5);
        Assert.True(Phase(phases, "postings").CoveredByR5);
    }

    [Fact]
    public void BuildPhases_FlagsNegativeDeltaInsteadOfHidingIt()
    {
        var phases = PhaseSplitRunner.BuildPhases(
        [
            Stage("enumerate", 100, 1_000),
            Stage("distinct", 400, 5_000),
            Stage("tokens", 900, 9_000),
            Stage("token_sets", 1_600, 20_000),
            Stage("postings", 4_200, 60_000),
            Stage("production", 4_000, 80_000)
        ]);

        Assert.True(Phase(phases, "children_publish").NegativeDelta);
        Assert.True(Phase(phases, "children_publish").Nanoseconds < 0);
    }

    private static PhaseStageSample Stage(
        string stage,
        double nanoseconds,
        double allocatedBytes) =>
        new(stage, nanoseconds, allocatedBytes, [nanoseconds], [allocatedBytes]);

    private static PhaseShare Phase(IReadOnlyList<PhaseShare> phases, string name) =>
        phases.Single(phase => phase.Phase == name);
}
