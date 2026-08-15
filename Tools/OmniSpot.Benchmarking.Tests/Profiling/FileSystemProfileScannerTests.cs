using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using OmniSpot.Benchmarking.Profiling;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Profiling;

public sealed class FileSystemProfileScannerTests
{
    [Fact]
    public void Scan_ComputesExactTokenDocumentFrequencies()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("ROOT");
        File.WriteAllText(Path.Combine(root, "ORTAK alpha.txt"), "a");
        File.WriteAllText(Path.Combine(root, "ORTAK beta.txt"), "b");
        File.WriteAllText(Path.Combine(root, "benzersiz.txt"), "c");

        var tokens = ProfileTestFixture.Scan(root).Metrics.Tokens;

        Assert.Equal(4, tokens.ItemsWithTokens);
        Assert.Equal(6, tokens.UniqueTokenCount);
        Assert.Equal(9, tokens.TokenItemEdgeCount);
        Assert.Equal(4d / 6, tokens.SingletonTokenRatio, 10);
        Assert.Equal(3d / 9, tokens.DuplicateAssignmentRatio, 10);
        Assert.Equal(5d / 9, tokens.SharedTokenEdgeRatio, 10);
        Assert.Equal(1, tokens.DocumentFrequency.P50);
        Assert.Equal(3, tokens.DocumentFrequency.P95);
        Assert.Equal(3, tokens.DocumentFrequency.Max);
        AssertBucket(tokens, "1", 4, 4);
        AssertBucket(tokens, "2", 1, 2);
        AssertBucket(tokens, "3-4", 1, 3);
    }

    [Fact]
    public void Scan_ComputesTurkishAndCaseIndicatorsFromNameCores()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("ROOT");
        File.WriteAllText(Path.Combine(root, "IŞIK.txt"), "a");
        File.WriteAllText(Path.Combine(root, "İzmir.txt"), "b");
        File.WriteAllText(Path.Combine(root, "ascii.txt"), "c");

        var names = ProfileTestFixture.Scan(root).Metrics.Names;

        Assert.Equal(4, names.TotalNameCount);
        Assert.Equal(4, names.LetteredNameCount);
        Assert.Equal(2, names.TurkishSpecificNameCount);
        Assert.Equal(3, names.DottedOrDotlessINameCount);
        Assert.Equal(2, names.AllUppercaseNameCount);
        Assert.Equal(2, names.CultureFoldDifferenceNameCount);
        Assert.Equal(0.5, names.CultureFoldDifferenceNameRatio);
        Assert.Equal(2, names.AsciiOnlyNameCount);
        Assert.Equal(2, names.NonAsciiNameCount);
    }

    [Fact]
    public void SerializedOutputs_DoNotExposePathNameTokenOrRareExtension()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("MAHREM-KOK");
        const string fileName = "GizliKisi-Özel-123.ultra-private-secret";
        File.WriteAllText(Path.Combine(root, fileName), "çok gizli içerik");

        var document = ProfileTestFixture.Scan(root);
        var json = ProfileJson.Serialize(document);
        var summary = ProfileSummaryFormatter.Format(document);

        Assert.Empty(document.Metrics.Extensions.Published);
        Assert.Equal(1, document.Metrics.Extensions.OtherFileCount);
        Assert.DoesNotContain(root, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileName, json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ultra-private-secret", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("gizlikisi", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(root, summary, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain(fileName, summary, StringComparison.OrdinalIgnoreCase);
        using var parsed = JsonDocument.Parse(json);
        Assert.Equal(
            document.MetricsFingerprint,
            parsed.RootElement.GetProperty("metrics_fingerprint").GetString());
        Assert.False(
            parsed.RootElement.GetProperty("metrics")
                .TryGetProperty("metrics_fingerprint", out _));
        var names = parsed.RootElement.GetProperty("metrics").GetProperty("names");
        Assert.True(names.TryGetProperty("culture_fold_difference_name_count", out _));
        Assert.True(names.TryGetProperty("culture_fold_difference_name_ratio", out _));
        Assert.False(names.TryGetProperty("culture_fold_difference_count", out _));
        Assert.False(names.TryGetProperty("culture_fold_difference_ratio", out _));
        var environment = parsed.RootElement
            .GetProperty("manifest")
            .GetProperty("environment");
        Assert.True(environment.TryGetProperty("processor_throttle_max_ac_start_percent", out _));
        Assert.True(environment.TryGetProperty("processor_throttle_max_dc_start_percent", out _));
        Assert.True(environment.TryGetProperty("processor_frequency_start_mhz", out _));
        Assert.True(environment.TryGetProperty("processor_frequency_end_mhz", out _));
        Assert.True(environment.TryGetProperty("processor_frequency_drift_percent", out _));
        Assert.True(environment.TryGetProperty("labels", out _));
    }

    [Fact]
    public void Metrics_AreDeterministicForTheSameTree()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("ROOT");
        Directory.CreateDirectory(Path.Combine(root, "alt"));
        File.WriteAllText(Path.Combine(root, "zeta.txt"), "z");
        File.WriteAllText(Path.Combine(root, "alt", "alfa.dat"), "a");

        var first = ProfileTestFixture.Scan(root);
        var second = ProfileTestFixture.Scan(root);
        var firstMetrics = ProfileJson.SerializeMetrics(first.Metrics);
        var secondMetrics = ProfileJson.SerializeMetrics(second.Metrics);

        Assert.Equal(firstMetrics, secondMetrics);
        Assert.Equal(first.MetricsFingerprint, second.MetricsFingerprint);
        Assert.Equal(
            ProfileJson.ComputeMetricsFingerprint(first.Metrics),
            first.MetricsFingerprint);
        Assert.Matches("^[0-9a-f]{64}$", first.MetricsFingerprint);
        Assert.Equal(2, first.SchemaMajor);
        Assert.Equal(1, first.SchemaMinor);
        Assert.Equal("0.4.0", first.ProfilerVersion);

        var changedMetrics = first.Metrics with
        {
            TotalItemCount = first.Metrics.TotalItemCount + 1
        };
        Assert.NotEqual(
            first.MetricsFingerprint,
            ProfileJson.ComputeMetricsFingerprint(changedMetrics));

        var changedEnvironment = first with
        {
            Manifest = first.Manifest with
            {
                Environment = first.Manifest.Environment with
                {
                    ProcessorFrequencyStartMhz = 4_252,
                    ProcessorFrequencyEndMhz = 4_252
                }
            }
        };
        Assert.Equal(first.MetricsFingerprint, changedEnvironment.MetricsFingerprint);
        Assert.NotEqual(ProfileJson.Serialize(first), ProfileJson.Serialize(changedEnvironment));
    }

    [Fact]
    public void Fingerprint_UsesCompactCanonicalJsonWithoutLineEndings()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("ROOT");
        File.WriteAllText(Path.Combine(root, "örnek.txt"), "x");
        var document = ProfileTestFixture.Scan(root);

        var canonical = ProfileJson.SerializeCanonicalMetrics(document.Metrics);
        var expected = Convert.ToHexString(
                SHA256.HashData(Encoding.UTF8.GetBytes(canonical)))
            .ToLowerInvariant();

        Assert.DoesNotContain('\r', canonical);
        Assert.DoesNotContain('\n', canonical);
        Assert.NotEqual(ProfileJson.SerializeMetrics(document.Metrics), canonical);
        Assert.Equal(expected, document.MetricsFingerprint);
    }

    [Fact]
    public void Scan_ReadsMetadataWithoutOpeningFileContent()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("ROOT");
        var path = Path.Combine(root, "kilitli.txt");
        File.WriteAllText(path, "içerik açılmamalı");
        using var locked = new FileStream(
            path,
            FileMode.Open,
            FileAccess.ReadWrite,
            FileShare.None);

        var document = ProfileTestFixture.Scan(root);

        Assert.Equal(1, document.Metrics.FileCount);
        Assert.Equal(0, document.Metrics.Errors.Sum(error => error.Count));
    }

    [Fact]
    public void Extensions_PublishOnlyWhenBothPrivacyThresholdsPass()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("ROOT");
        for (var index = 0; index < 50; index++)
        {
            File.WriteAllText(Path.Combine(root, "dosya-" + index + ".txt"), "x");
        }

        File.WriteAllText(Path.Combine(root, "tek.kisisel-uzanti"), "x");

        var extensions = ProfileTestFixture.Scan(root).Metrics.Extensions;

        var published = Assert.Single(extensions.Published);
        Assert.Equal(".txt", published.Extension);
        Assert.Equal(50, published.Count);
        Assert.Equal(1, extensions.OtherFileCount);
    }

    [Fact]
    public void Summary_ClosesExtensionCountsWhenPublishedValuesOverflow()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("ROOT");
        var document = ProfileTestFixture.Scan(root);
        var published = Enumerable.Range(0, 21)
            .Select(index => new ExtensionMetric(
                ".e" + index.ToString("D2"),
                Count: 50,
                FileRatio: 50d / 1050))
            .ToArray();
        var metrics = document.Metrics with
        {
            FileCount = 1050,
            Extensions = new ExtensionProfile(
                published,
                OtherFileCount: 0,
                NoExtensionFileCount: 0)
        };
        document = document with
        {
            Metrics = metrics,
            MetricsFingerprint = ProfileJson.ComputeMetricsFingerprint(metrics)
        };

        var summary = ProfileSummaryFormatter.Format(document);

        Assert.Contains(
            "... 1 uzantı daha (50 dosya, 4.76 %) yalnız JSON'da",
            summary,
            StringComparison.Ordinal);
        Assert.Equal(
            metrics.FileCount,
            published.Take(20).Sum(extension => extension.Count) +
            published.Skip(20).Sum(extension => extension.Count) +
            metrics.Extensions.OtherFileCount +
            metrics.Extensions.NoExtensionFileCount);
    }

    [Fact]
    public void Summary_StaysWithinHumanAuditLimits()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("ROOT");
        File.WriteAllText(Path.Combine(root, "örnek belge.txt"), "x");

        var summary = ProfileSummaryFormatter.Format(ProfileTestFixture.Scan(root));

        Assert.True(summary.Split('\n').Length <= ProfileSummaryFormatter.MaximumLineCount);
        Assert.True(Encoding.UTF8.GetByteCount(summary) <= ProfileSummaryFormatter.MaximumUtf8Bytes);
        Assert.Contains("Metrics SHA-256:", summary, StringComparison.Ordinal);
    }

    [Fact]
    public void ReparseSubtypeCounts_AreUnavailableInsteadOfMisleadingZeroes()
    {
        using var fixture = new ProfileTestFixture();
        var root = fixture.CreateRoot("ROOT");
        var document = ProfileTestFixture.Scan(root);
        var special = document.Metrics.SpecialCases with
        {
            ReparsePointCount = 1,
            JunctionCount = null,
            SymbolicLinkCount = null,
            OtherReparsePointCount = null
        };
        document = document with
        {
            Metrics = document.Metrics with { SpecialCases = special }
        };

        var json = ProfileJson.Serialize(document);
        var summary = ProfileSummaryFormatter.Format(document);

        using var parsed = JsonDocument.Parse(json);
        var junctionCount = parsed.RootElement
            .GetProperty("metrics")
            .GetProperty("special_cases")
            .GetProperty("junction_count");
        Assert.Equal(JsonValueKind.Null, junctionCount.ValueKind);
        Assert.Contains("junction/symlink/other ayrımı ölçülmedi", summary, StringComparison.Ordinal);
        Assert.DoesNotContain("junction=0", summary, StringComparison.Ordinal);
    }

    private static void AssertBucket(
        TokenProfile profile,
        string label,
        long tokenCount,
        long edgeCount)
    {
        var bucket = Assert.Single(profile.FanOutHistogram, item => item.Label == label);
        Assert.Equal(tokenCount, bucket.TokenCount);
        Assert.Equal(edgeCount, bucket.TokenItemEdgeCount);
    }
}
