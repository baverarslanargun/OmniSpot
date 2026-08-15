using OmniSpot.Benchmarking.Measurements;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Measurements;

public sealed class SyntheticSearchFixtureTests
{
    [Fact]
    public void Create_SameInputsProduceSameFingerprint()
    {
        var first = SyntheticSearchFixtureGenerator.Create(1_000, 1701);
        var second = SyntheticSearchFixtureGenerator.Create(1_000, 1701);

        Assert.Equal(first.Manifest, second.Manifest);
        Assert.Equal(1_000, first.Nodes.Count);
        Assert.Equal(
            first.Nodes.Select(node => (node.Name, node.FullPath, node.IsDirectory)),
            second.Nodes.Select(node => (node.Name, node.FullPath, node.IsDirectory)));
    }

    [Fact]
    public void Create_DifferentSeedChangesFingerprint()
    {
        var first = SyntheticSearchFixtureGenerator.Create(1_000, 1701);
        var second = SyntheticSearchFixtureGenerator.Create(1_000, 1702);

        Assert.NotEqual(first.Manifest.Fingerprint, second.Manifest.Fingerprint);
    }

    [Fact]
    public void SerializedMeasurementContainsFingerprintButNoFixtureNamesOrPaths()
    {
        var fixture = SyntheticSearchFixtureGenerator.Create(10, 1701);
        var document = MeasurementTestData.CreateDocument(fixture.Manifest, p95Nanoseconds: 100);

        var json = MeasurementJson.Serialize(document);

        Assert.Contains(fixture.Manifest.Fingerprint, json, StringComparison.Ordinal);
        foreach (var node in fixture.Nodes)
        {
            Assert.DoesNotContain(node.Name, json, StringComparison.Ordinal);
            Assert.DoesNotContain(node.FullPath, json, StringComparison.Ordinal);
        }
    }
}
