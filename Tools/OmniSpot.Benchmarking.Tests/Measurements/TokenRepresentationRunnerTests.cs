using System.Text.Json;
using OmniSpot.Benchmarking.Measurements;
using OmniSpot.Benchmarking.Profiling;
using OmniSpot.Benchmarking.Tests.Profiling;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class TokenRepresentationRunnerTests
{
    private const int ItemCount = 2_000;
    private const int Seed = 1701;
    private const int Rounds = 1;

    private static readonly string[] ExpectedVariants =
    [
        "hashset",
        "array",
        "pooled_array"
    ];

    // GC ölçümleri makineye göre değişir; test rakama değil yapıya bakar.
    [Fact]
    public void Run_ReportsEveryVariantOnce()
    {
        var comparison = RunComparison();

        Assert.Equal(
            ExpectedVariants,
            comparison.Variants.Select(variant => variant.Variant).ToArray());
        Assert.All(comparison.Variants, variant =>
            Assert.False(string.IsNullOrWhiteSpace(variant.Scope)));
    }

    // Eşleştirme ancak sıra dengeliyse anlamlıdır: her temsil aynı sayıda
    // örnek almalı ve hiçbiri sistematik olarak ilk sırada olmamalı.
    [Fact]
    public void Run_GivesEveryVariantTheSameNumberOfPairedSamples()
    {
        var comparison = RunComparison();

        var counts = comparison.Samples
            .GroupBy(sample => sample.Variant)
            .ToDictionary(group => group.Key, group => group.Count());
        Assert.Equal(ExpectedVariants.Length, counts.Count);
        Assert.Single(counts.Values.Distinct());

        // Eşleştirme her tur içinde aynalı olmalı; tur sayısı artınca da
        // geçerli kalsın diye tur tur bakılır, tüm diziye değil.
        var perRound = ExpectedVariants.Length * 2;
        Assert.Equal(0, comparison.Samples.Count % perRound);
        foreach (var round in comparison.Samples.Chunk(perRound))
        {
            var order = round.Select(sample => sample.Variant).ToArray();
            Assert.Equal(order.Reverse().ToArray(), order);
        }
    }

    // GC deltası küçük ağaçlarda negatife düşebilir. Test bunu yasaklamaz;
    // yasakladığı şey negatifin ölçülmüş bir değer gibi raporlanmasıdır.
    [Fact]
    public void Run_ReportsNullInsteadOfNegativeForUnmeasurableSamples()
    {
        var comparison = RunComparison();

        Assert.All(comparison.Samples, sample =>
            Assert.True(sample.RetainedBytes is null or > 0));
        Assert.All(comparison.Variants, variant =>
        {
            Assert.True(variant.MedianRetainedBytes is null or > 0);
            Assert.Equal(
                variant.MeasuredSampleCount == 0,
                variant.MedianRetainedBytes is null);
        });
    }

    // Ölçülemeyen temsil varsa yüzde değişimi üretilmez ve bu bir kabul
    // hatası olarak raporlanır; eksik karşılaştırma tam gibi sunulamaz.
    [Fact]
    public void Run_SuppressesChangePercentWhenAVariantIsUnmeasurable()
    {
        var comparison = RunComparison();

        if (comparison.Variants.All(variant => variant.MedianRetainedBytes is not null))
        {
            Assert.NotNull(comparison.ArrayChangePercent);
            Assert.NotNull(comparison.PooledArrayChangePercent);
        }
        else
        {
            Assert.Contains(
                comparison.AcceptanceFailures,
                failure => failure.Contains("ölçülemedi", StringComparison.Ordinal));
        }
    }

    // Asıl kapı bu: aday temsiller üretimin OrdinalIgnoreCase benzersiz küme
    // semantiğini bozarsa sayılar anlamsızdır. Sentetik fixture'da bozulmamalı.
    [Fact]
    public void Run_AcceptsCandidatesThatPreserveSetSemantics()
    {
        var comparison = RunComparison();

        Assert.DoesNotContain(
            comparison.AcceptanceFailures,
            failure =>
                failure.Contains("token kümesini değiştirdi", StringComparison.Ordinal) ||
                failure.Contains("yinelenen token", StringComparison.Ordinal) ||
                failure.Contains("havuzla paylaşılmıyor", StringComparison.Ordinal));
    }

    [Fact]
    public void Run_CarriesSchemaVersionAndEnvironmentManifest()
    {
        var comparison = RunComparison();

        Assert.True(comparison.SchemaMajor > 0);
        Assert.True(comparison.SchemaMinor >= 0);
        Assert.NotNull(comparison.Environment);
        Assert.Equal("test-os", comparison.Environment.OsDescription);
        Assert.Equal("instrumented_profiler", comparison.Lane);
    }

    [Fact]
    public void Run_CountsMatchTheFixture()
    {
        var comparison = RunComparison();

        Assert.Equal(ItemCount, comparison.Facts.NodeCount);
        Assert.Equal(ItemCount, comparison.Facts.DistinctItemCount);
        Assert.True(comparison.Facts.UniqueTokenCount > 0);
        Assert.True(comparison.Facts.DistinctTokenLinkCount >= comparison.Facts.UniqueTokenCount);
        Assert.True(comparison.Facts.TokenOccurrenceCount >= comparison.Facts.DistinctTokenLinkCount);
        Assert.True(comparison.Facts.MaxTokensPerItem > 0);
    }

    // Kalıcı çıktı sözleşmesi: ad, token ve path hiçbir alana sızmamalı.
    [Fact]
    public void Run_JsonCarriesNoNameTokenOrPathValues()
    {
        var comparison = RunComparison();

        var json = MeasurementJson.Serialize(comparison);
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

    private static TokenRepresentationComparison RunComparison() =>
        TokenRepresentationRunner.Run(
            SyntheticSearchFixtureGenerator.Create(ItemCount, Seed).Nodes,
            Rounds,
            enumerationMilliseconds: 0,
            TimeSpan.FromMinutes(2),
            ProfileEnvironmentCapture.FromCompleted(ProfileTestFixture.CreateEnvironment()),
            CancellationToken.None);
}
