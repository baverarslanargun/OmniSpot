using SmartFileLauncher.Core.Diagnostics;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Diagnostics;

public class DiagnosticsMetricsTests
{
    [Fact]
    public void GroupsKeepInsertionOrder()
    {
        var metrics = new DiagnosticsMetrics();

        metrics.Set("SÜREÇ", "private", "1");
        metrics.Set("İNDEKS", "dosya", "2");
        metrics.Set("KÜÇÜK RESİM", "önbellek", "3");

        Assert.Equal(
            new[] { "SÜREÇ", "İNDEKS", "KÜÇÜK RESİM" },
            metrics.Snapshot().Select(group => group.Title));
    }

    [Fact]
    public void ReadingsKeepInsertionOrderWithinGroup()
    {
        var metrics = new DiagnosticsMetrics();

        metrics.Set("SÜREÇ", "private", "1");
        metrics.Set("SÜREÇ", "working set", "2");
        metrics.Set("SÜREÇ", "handle", "3");

        Assert.Equal(
            new[] { "private", "working set", "handle" },
            metrics.Snapshot().Single().Readings.Select(reading => reading.Label));
    }

    [Fact]
    public void UpdatingExistingLabelReplacesValueWithoutReordering()
    {
        var metrics = new DiagnosticsMetrics();
        metrics.Set("SÜREÇ", "private", "1");
        metrics.Set("SÜREÇ", "working set", "2");

        metrics.Set("SÜREÇ", "private", "9");

        var readings = metrics.Snapshot().Single().Readings;
        Assert.Equal(2, readings.Count);
        Assert.Equal("private", readings[0].Label);
        Assert.Equal("9", readings[0].Value);
        Assert.Equal("working set", readings[1].Label);
    }

    [Fact]
    public void RevisionStaysStableWhenOnlyValuesChange()
    {
        var metrics = new DiagnosticsMetrics();
        metrics.Set("SÜREÇ", "private", "1");
        metrics.Set("SÜREÇ", "working set", "2");
        var revision = metrics.Revision;

        metrics.Set("SÜREÇ", "private", "9");
        metrics.Set("SÜREÇ", "working set", "8");

        Assert.Equal(revision, metrics.Revision);
    }

    [Fact]
    public void RevisionAdvancesWhenNewLabelAppears()
    {
        var metrics = new DiagnosticsMetrics();
        metrics.Set("SÜREÇ", "private", "1");
        var revision = metrics.Revision;

        metrics.Set("SÜREÇ", "handle", "2");

        Assert.True(metrics.Revision > revision);
    }

    [Fact]
    public void SeverityIsCarriedThrough()
    {
        var metrics = new DiagnosticsMetrics();

        metrics.Set("KÜÇÜK RESİM", "önbellek", "1000/1000", DiagnosticsSeverity.Critical);

        Assert.Equal(
            DiagnosticsSeverity.Critical,
            metrics.Snapshot().Single().Readings.Single().Severity);
    }

    [Fact]
    public void ConcurrentWritersProduceOneReadingPerLabel()
    {
        var metrics = new DiagnosticsMetrics();
        var labels = Enumerable.Range(0, 50).Select(index => $"m{index}").ToArray();

        Parallel.For(0, 8, _ =>
        {
            for (var round = 0; round < 20; round++)
            {
                foreach (var label in labels)
                {
                    metrics.Set("SÜREÇ", label, round.ToString());
                }
            }
        });

        var readings = metrics.Snapshot().Single().Readings;
        Assert.Equal(labels.Length, readings.Count);
        Assert.Equal(labels, readings.Select(reading => reading.Label));
    }

    [Fact]
    public void SnapshotDoesNotObserveLaterWrites()
    {
        var metrics = new DiagnosticsMetrics();
        metrics.Set("SÜREÇ", "private", "1");

        var snapshot = metrics.Snapshot();
        metrics.Set("SÜREÇ", "private", "2");
        metrics.Set("SÜREÇ", "handle", "3");

        Assert.Equal("1", snapshot.Single().Readings.Single().Value);
    }
}
