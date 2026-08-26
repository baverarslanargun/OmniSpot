using OmniSpot.Benchmarking.Diagnostics;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Diagnostics;

public sealed class MetricLogParserTests
{
    [Fact]
    public void SkipsHeaderLine()
    {
        var result = MetricLogParser.Parse(["zaman;bölüm;etiket;değer;sayısal"]);

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void ParsesRowFields()
    {
        var result = MetricLogParser.Parse(
            ["2026-08-27 01:15:04.250;BELLEK;toplam ayrılan;1,42 GB;1524713472"]);

        var row = Assert.Single(result.Rows);
        Assert.Equal(new DateTime(2026, 8, 27, 1, 15, 4, 250), row.Timestamp);
        Assert.Equal("BELLEK", row.Group);
        Assert.Equal("toplam ayrılan", row.Label);
        Assert.Equal("1,42 GB", row.Value);
        Assert.Equal(1524713472d, row.Numeric);
    }

    [Fact]
    public void ParsesNumericWithInvariantDecimalPoint()
    {
        var result = MetricLogParser.Parse(
            ["2026-08-27 01:15:04.250;ARAMA;son süre;12,5 ms;12.5"]);

        var row = Assert.Single(result.Rows);
        Assert.Equal(12.5d, row.Numeric);
    }

    [Fact]
    public void CountsWrongFieldCountAsSkipped()
    {
        var result = MetricLogParser.Parse(
            ["2026-08-27 01:15:04.250;SÜREÇ;working set;1035,2 MB"]);

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.SkippedLines);
    }

    [Fact]
    public void CountsUnparseableNumericAsSkipped()
    {
        var result = MetricLogParser.Parse(
            ["2026-08-27 01:15:04.250;SÜREÇ;working set;1035,2 MB;1085276160,5"]);

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.SkippedLines);
    }

    [Fact]
    public void CountsUnparseableTimestampAsSkipped()
    {
        var result = MetricLogParser.Parse(
            ["27.08.2026 01:15:04;SÜREÇ;working set;1035,2 MB;1085276160"]);

        Assert.Empty(result.Rows);
        Assert.Equal(1, result.SkippedLines);
    }

    [Fact]
    public void TreatsEmptyNumericAsNull()
    {
        var result = MetricLogParser.Parse(
            ["2026-08-27 01:15:04.250;İNDEKS;son tur;01:04:50;"]);

        var row = Assert.Single(result.Rows);
        Assert.Null(row.Numeric);
        Assert.Equal(0, result.SkippedLines);
    }

    [Fact]
    public void IdentifiesEventRows()
    {
        var result = MetricLogParser.Parse(
        [
            "2026-08-27 01:15:04.250;OLAY;uzlaştırma bitti;3 değişiklik;3",
            "2026-08-27 01:15:04.251;BELLEK;yönetilen yığın;120,0 MB;125829120"
        ]);

        Assert.True(result.Rows[0].IsEvent);
        Assert.False(result.Rows[1].IsEvent);
    }

    [Fact]
    public void SortsRowsByTimestamp()
    {
        var result = MetricLogParser.Parse(
        [
            "2026-08-27 01:15:04.250;SÜREÇ;handle;900;900",
            "2026-08-27 01:14:04.250;SÜREÇ;handle;880;880"
        ]);

        Assert.Equal(880d, result.Rows[0].Numeric);
        Assert.Equal(900d, result.Rows[1].Numeric);
    }

    [Fact]
    public void IgnoresBlankLines()
    {
        var result = MetricLogParser.Parse(["", "   "]);

        Assert.Empty(result.Rows);
        Assert.Equal(0, result.SkippedLines);
    }
}
