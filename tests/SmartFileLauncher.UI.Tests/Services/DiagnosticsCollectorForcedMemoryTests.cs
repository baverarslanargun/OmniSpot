using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

/// <summary>
/// `yönetilen yığın` toplama zorlamadan okunduğu için canlı veri ile
/// toplanmamış çöp aynı sayıya giriyor. Zorlanmış örnek bu ayrımı verir; ama
/// toplama uygulamayı durdurduğundan üretim varsayılanında hiç çalışmamalıdır.
/// </summary>
public sealed class DiagnosticsCollectorForcedMemoryTests
{
    private const string LiveLabel = "canlı yığın (zorlanmış)";

    [Fact]
    public void ForcedCollectionIsOffByDefault()
    {
        var collector = Create(out _, interval: null);

        collector.Refresh();
        collector.Refresh();

        Assert.Null(Find(collector, LiveLabel));
        Assert.Null(Find(collector, "  toplanan"));
        Assert.Null(Find(collector, "  toplama süresi"));
    }

    [Fact]
    public void FirstRefreshOnlyArmsTheSampleWithoutCollecting()
    {
        var collector = Create(out _, TimeSpan.FromSeconds(60));
        var before = GC.CollectionCount(2);

        collector.Refresh();

        var reading = Find(collector, LiveLabel);
        Assert.NotNull(reading);
        Assert.Equal("bekleniyor", reading!.Value);
        Assert.Null(reading.Numeric);
        Assert.Equal(before, GC.CollectionCount(2));
    }

    [Fact]
    public void SampleIsTakenOnlyAfterTheIntervalElapses()
    {
        var collector = Create(out var clock, TimeSpan.FromSeconds(60));

        collector.Refresh();
        clock.Advance(TimeSpan.FromSeconds(59));
        collector.Refresh();

        Assert.Equal("bekleniyor", Find(collector, LiveLabel)!.Value);

        clock.Advance(TimeSpan.FromSeconds(2));
        collector.Refresh();

        var live = Find(collector, LiveLabel);
        Assert.NotNull(live);
        Assert.NotEqual("bekleniyor", live!.Value);
        Assert.NotNull(live.Numeric);
        Assert.True(live.Numeric > 0);

        Assert.NotNull(Find(collector, "  toplanan"));
        var duration = Find(collector, "  toplama süresi");
        Assert.NotNull(duration);
        Assert.NotNull(duration!.Numeric);
        Assert.True(duration.Numeric >= 0);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void NonPositiveIntervalIsRejected(int seconds)
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => Create(out _, TimeSpan.FromSeconds(seconds)));
    }

    private static DiagnosticsCollector Create(out TestClock clock, TimeSpan? interval)
    {
        clock = new TestClock(new DateTime(2026, 8, 28, 5, 0, 0, DateTimeKind.Local));
        var local = clock;
        return new DiagnosticsCollector(
            new FakeIndexLifecycle(),
            new FakeThumbnailService(),
            128,
            1000,
            () => local.Now,
            interval);
    }

    private static DiagnosticsReading? Find(DiagnosticsCollector collector, string label) =>
        collector.Metrics.Snapshot()
            .FirstOrDefault(group => group.Title == DiagnosticsCollector.GroupMemory)
            ?.Readings.FirstOrDefault(reading => reading.Label == label);

    private sealed class TestClock(DateTime start)
    {
        public DateTime Now { get; private set; } = start;

        public void Advance(TimeSpan amount) => Now += amount;
    }
}
