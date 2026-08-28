using System.Text.Json;
using OmniSpot.Benchmarking.Measurements;
using OmniSpot.Benchmarking.Profiling;
using OmniSpot.Benchmarking.Tests.Profiling;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class RealTreeMemoryBreakdownTests
{
    private const int ItemCount = 2_000;
    private const int Seed = 1701;

    private static readonly string[] ExpectedIndexStages =
    [
        "node_tree",
        "node_metadata",
        "path_to_node",
        "metadata_map"
    ];

    private static readonly string[] ExpectedStages =
    [
        "search_items",
        "token_sets",
        "items_by_path",
        "tokens_by_path",
        "paths_by_token",
        "children_by_path"
    ];

    // GC ölçümleri makineye göre değişir; test rakama değil yapıya bakar.
    [Fact]
    public void Run_ReportsEveryExpectedStageOnce()
    {
        var breakdown = RunBreakdown();

        Assert.Equal(
            ExpectedStages,
            breakdown.Stages.Select(stage => stage.Stage).ToArray());
        Assert.All(breakdown.Stages, stage => Assert.False(string.IsNullOrWhiteSpace(stage.Scope)));
    }

    // GC deltası küçük ağaçlarda negatife düşebilir. Test bunu yasaklamaz;
    // yasakladığı şey negatifin ölçülmüş bir değer gibi raporlanmasıdır:
    // ölçülemeyen aşamanın değeri null olmalı, ölçülebilenin pozitif.
    [Fact]
    public void Run_ReportsNullInsteadOfNegativeForUnmeasurableStages()
    {
        var breakdown = RunBreakdown();

        Assert.All(breakdown.Stages, stage =>
        {
            if (stage.Measurable)
            {
                Assert.NotNull(stage.RetainedBytes);
                Assert.True(stage.RetainedBytes > 0);
            }
            else
            {
                Assert.Null(stage.RetainedBytes);
            }
        });

        Assert.True(breakdown.FullCreateRetainedBytes is null or > 0);
    }

    // Ölçülemeyen aşama varsa toplam ve çapraz denetim üretilmez; eksik
    // toplamı tam sayıymış gibi raporlamak yanıltıcı olurdu.
    [Fact]
    public void Run_SuppressesTotalWhenAnyStageIsUnmeasurable()
    {
        var breakdown = RunBreakdown();

        if (breakdown.Stages.All(stage => stage.Measurable))
        {
            Assert.NotNull(breakdown.BreakdownTotalBytes);
            Assert.Equal(
                breakdown.Stages.Sum(stage => stage.RetainedBytes!.Value),
                breakdown.BreakdownTotalBytes);
        }
        else
        {
            Assert.Null(breakdown.BreakdownTotalBytes);
            Assert.Null(breakdown.CrossCheckDeltaPercent);
        }
    }

    [Fact]
    public void Run_CarriesSchemaVersionAndEnvironmentManifest()
    {
        var breakdown = RunBreakdown();

        Assert.True(breakdown.SchemaMajor > 0);
        Assert.True(breakdown.SchemaMinor >= 0);
        Assert.NotNull(breakdown.Environment);
        Assert.Equal("test-os", breakdown.Environment.OsDescription);
    }

    [Fact]
    public void Run_CountsMatchTheFixture()
    {
        var breakdown = RunBreakdown();

        Assert.Equal(ItemCount, breakdown.NodeCount);
        Assert.Equal(ItemCount, breakdown.DistinctItemCount);
        Assert.True(breakdown.UniqueTokenCount > 0);
        Assert.True(breakdown.TokenToItemLinkCount >= breakdown.UniqueTokenCount);
    }

    [Fact]
    public void Run_ReportsEveryExpectedIndexStageOnce()
    {
        var breakdown = RunBreakdown();

        Assert.Equal(
            ExpectedIndexStages,
            breakdown.IndexStages.Select(stage => stage.Stage).ToArray());
        Assert.All(
            breakdown.IndexStages,
            stage => Assert.False(string.IsNullOrWhiteSpace(stage.Scope)));
    }

    [Fact]
    public void Run_ReportsNullInsteadOfNegativeForUnmeasurableIndexStages()
    {
        var breakdown = RunBreakdown();

        Assert.All(breakdown.IndexStages, stage =>
        {
            if (stage.Measurable)
            {
                Assert.NotNull(stage.RetainedBytes);
                Assert.True(stage.RetainedBytes > 0);
            }
            else
            {
                Assert.Null(stage.RetainedBytes);
            }
        });
    }

    [Fact]
    public void Run_SuppressesIndexTotalWhenAnyIndexStageIsUnmeasurable()
    {
        var breakdown = RunBreakdown();

        if (breakdown.IndexStages.All(stage => stage.Measurable))
        {
            Assert.NotNull(breakdown.IndexStagesTotalBytes);
            Assert.Equal(
                breakdown.IndexStages.Sum(stage => stage.RetainedBytes!.Value),
                breakdown.IndexStagesTotalBytes);
        }
        else
        {
            Assert.Null(breakdown.IndexStagesTotalBytes);
        }
    }

    // Ölçülen her aşama artık üretimde boşta da bellekte duran bir yapı.
    // Kararlı toplam, tam `SearchState` ile `IndexManager` aşamalarının
    // toplamıdır; hiçbir aşama dışarıda bırakılmaz.
    [Fact]
    public void Run_SteadyManagedTotalIsFullCreatePlusEveryIndexStage()
    {
        var breakdown = RunBreakdown();

        if (breakdown.FullCreateRetainedBytes is > 0 &&
            breakdown.IndexStagesTotalBytes is not null)
        {
            Assert.Equal(
                breakdown.FullCreateRetainedBytes!.Value +
                    breakdown.IndexStagesTotalBytes!.Value,
                breakdown.SteadyManagedTotalBytes);
        }
        else
        {
            Assert.Null(breakdown.SteadyManagedTotalBytes);
        }
    }

    // Kalıcı çıktı sözleşmesi: ad, token ve path hiçbir alana sızmamalı.
    [Fact]
    public void Run_JsonCarriesNoNameTokenOrPathValues()
    {
        var breakdown = RunBreakdown();

        var json = MeasurementJson.Serialize(breakdown);
        using var document = JsonDocument.Parse(json);
        foreach (var value in EnumerateStrings(document.RootElement))
        {
            Assert.DoesNotContain(@"\", value, StringComparison.Ordinal);
            Assert.DoesNotContain(":/", value, StringComparison.Ordinal);
            Assert.DoesNotContain("OmniSpotSynthetic", value, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(".pdf", value, StringComparison.OrdinalIgnoreCase);
        }
    }

    private static IEnumerable<string> EnumerateStrings(JsonElement element)
    {
        switch (element.ValueKind)
        {
            case JsonValueKind.String:
                yield return element.GetString() ?? string.Empty;
                break;
            case JsonValueKind.Object:
                foreach (var property in element.EnumerateObject())
                {
                    yield return property.Name;
                    foreach (var value in EnumerateStrings(property.Value))
                    {
                        yield return value;
                    }
                }

                break;
            case JsonValueKind.Array:
                foreach (var item in element.EnumerateArray())
                {
                    foreach (var value in EnumerateStrings(item))
                    {
                        yield return value;
                    }
                }

                break;
        }
    }

    private static MemoryBreakdown RunBreakdown() =>
        RealTreeMemoryBreakdown.Run(
            SyntheticSearchFixtureGenerator.Create(ItemCount, Seed).Nodes,
            ProfileEnvironmentCapture.FromCompleted(ProfileTestFixture.CreateEnvironment()),
            CancellationToken.None);
}
