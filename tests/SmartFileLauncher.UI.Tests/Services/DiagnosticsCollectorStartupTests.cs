using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class DiagnosticsCollectorStartupTests
{
    [Fact]
    public void SamplesProcessMetricsWithoutReadingDatabaseUntilIndexIsReady()
    {
        var lifecycle = new FakeIndexLifecycle { IsInitialized = false };
        var collector = new DiagnosticsCollector(lifecycle, new FakeThumbnailService(), 128, 1000);

        collector.Refresh();

        Assert.Equal(0, lifecycle.StatsCalls);
        var starting = collector.Metrics.Snapshot();
        Assert.Contains(starting, group => group.Title == DiagnosticsCollector.GroupProcess);
        Assert.Contains(starting.Single(group => group.Title == DiagnosticsCollector.GroupIndex).Readings,
            metric => metric.Label == "durum" && metric.Value == "hazırlanıyor");

        lifecycle.IsInitialized = true;
        collector.Refresh();

        Assert.Equal(1, lifecycle.StatsCalls);
        Assert.Contains(collector.Metrics.Snapshot().Single(group => group.Title == DiagnosticsCollector.GroupIndex).Readings,
            metric => metric.Label == "dosya" && metric.Numeric == 12);
    }
}
