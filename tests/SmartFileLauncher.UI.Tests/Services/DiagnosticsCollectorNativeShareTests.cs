using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

/// <summary>
/// Bellek büyümesinin yönetilen tarafta mı (bizim nesnelerimiz) yoksa native
/// tarafta mı (WPF bitmap'leri, SQLite, kabuk) olduğu tek satırda görünmeli;
/// aksi hâlde `private` ile yığın elle çıkarılmak zorunda kalıyor.
/// </summary>
public sealed class DiagnosticsCollectorNativeShareTests
{
    [Fact]
    public void NativeShareIsReportedAfterTheFirstRefresh()
    {
        var collector = CreateCollector();

        collector.Refresh();

        var reading = Read(collector, DiagnosticsCollector.GroupMemory, "native pay");
        Assert.NotNull(reading.Numeric);
        Assert.True(reading.Numeric >= 0d);
    }

    /// <summary>
    /// Satır türetilmiş bir değer: aynı turdaki `private` ve `yönetilen yığın`
    /// okumalarının farkına eşit olmalı, yoksa üç satır birbirini tutmaz.
    /// </summary>
    [Fact]
    public void NativeShareEqualsPrivateMinusManagedHeapInTheSameRound()
    {
        var collector = CreateCollector();

        collector.Refresh();

        var privateBytes = Read(collector, DiagnosticsCollector.GroupProcess, "private");
        var managed = Read(collector, DiagnosticsCollector.GroupMemory, "yönetilen yığın");
        var native = Read(collector, DiagnosticsCollector.GroupMemory, "native pay");

        Assert.NotNull(privateBytes.Numeric);
        Assert.NotNull(managed.Numeric);
        Assert.NotNull(native.Numeric);
        Assert.Equal(
            privateBytes.Numeric!.Value - managed.Numeric!.Value,
            native.Numeric!.Value);
    }

    private static DiagnosticsCollector CreateCollector()
        => new(new FakeIndexLifecycle(), new FakeThumbnailService(), 128, 1000);

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
