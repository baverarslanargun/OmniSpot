using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

/// <summary>
/// Tanılama yüzeyi kendi hatasını yapışkan biçimde raporlamamalıdır: başarısız
/// bir toplama turundan sonra gelen başarılı tur, durumu geri almalıdır.
/// </summary>
public sealed class DiagnosticsCollectorStatusTests
{
    [Fact]
    public void IndexStatusIsGoodWhenCollectionSucceeds()
    {
        var collector = CreateCollector(out _);

        collector.Refresh();

        var reading = Read(collector, DiagnosticsCollector.GroupIndex, "durum");
        Assert.Equal("iyi", reading.Value);
        Assert.Equal(DiagnosticsSeverity.Good, reading.Severity);
    }

    [Fact]
    public void IndexStatusRecoversAfterFailedCollection()
    {
        var collector = CreateCollector(out var lifecycle);
        lifecycle.StatsFailure = new InvalidOperationException("indeks henüz hazır değil");

        collector.Refresh();

        var failed = Read(collector, DiagnosticsCollector.GroupIndex, "durum");
        Assert.Equal(nameof(InvalidOperationException), failed.Value);
        Assert.Equal(DiagnosticsSeverity.Critical, failed.Severity);

        lifecycle.StatsFailure = null;
        collector.Refresh();

        var recovered = Read(collector, DiagnosticsCollector.GroupIndex, "durum");
        Assert.Equal("iyi", recovered.Value);
        Assert.Equal(DiagnosticsSeverity.Good, recovered.Severity);
    }

    [Fact]
    public void ProcessPrivateStaysNumericAndStatusIsReported()
    {
        var collector = CreateCollector(out _);

        collector.Refresh();

        var privateBytes = Read(collector, DiagnosticsCollector.GroupProcess, "private");
        Assert.NotNull(privateBytes.Numeric);
        Assert.True(privateBytes.Numeric > 0);

        var status = Read(collector, DiagnosticsCollector.GroupProcess, "durum");
        Assert.Equal("iyi", status.Value);
    }

    [Fact]
    public void IoStatusIsAlwaysPresent()
    {
        var collector = CreateCollector(out _);

        collector.Refresh();

        var status = Read(collector, DiagnosticsCollector.GroupIo, "durum");
        Assert.Contains(status.Value, new[] { "okundu", "okunamadı" });
    }

    private static DiagnosticsCollector CreateCollector(out FakeIndexLifecycle lifecycle)
    {
        lifecycle = new FakeIndexLifecycle();
        return new DiagnosticsCollector(lifecycle, new FakeThumbnailService(), 128, 1000);
    }

    private static DiagnosticsReading Read(
        DiagnosticsCollector collector,
        string group,
        string label)
    {
        var snapshot = collector.Metrics.Snapshot();
        var found = snapshot.SingleOrDefault(g => g.Title == group);
        Assert.NotNull(found);
        var reading = found!.Readings.SingleOrDefault(r => r.Label == label);
        Assert.True(reading is not null, $"'{group} / {label}' okuması bulunamadı.");
        return reading!;
    }
}
