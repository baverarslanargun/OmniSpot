using OmniSpot.Benchmarking.Diagnostics;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Diagnostics;

public sealed class MetricLogAnalyzerTests
{
    [Fact]
    public void EmptyInputProducesEmptyAnalysis()
    {
        var analysis = MetricLogAnalyzer.Analyze(MetricLogParser.Parse([]));

        Assert.Equal(0, analysis.RowCount);
        Assert.Empty(analysis.Windows);
        Assert.Empty(analysis.Events);
    }

    [Fact]
    public void WithoutEventsProducesSingleWindow()
    {
        var analysis = Analyze(
            Sample("01:00:00", "SÜREÇ", "working set", 100),
            Sample("01:00:05", "SÜREÇ", "working set", 180));

        var window = Assert.Single(analysis.Windows);
        Assert.Equal(MetricLogAnalyzer.FileStartLabel, window.StartLabel);
        Assert.Equal(MetricLogAnalyzer.FileEndLabel, window.EndLabel);
    }

    [Fact]
    public void EachEventAddsAWindow()
    {
        var analysis = Analyze(
            Sample("01:00:00", "SÜREÇ", "working set", 100),
            Event("01:00:05", "uzlaştırma başladı"),
            Sample("01:00:06", "SÜREÇ", "working set", 150),
            Event("01:00:10", "uzlaştırma bitti"),
            Sample("01:00:11", "SÜREÇ", "working set", 300));

        Assert.Equal(2, analysis.Events.Count);
        Assert.Equal(3, analysis.Windows.Count);
    }

    [Fact]
    public void DeltaIsLastMinusFirstWithinWindow()
    {
        var analysis = Analyze(
            Sample("01:00:00", "BELLEK", "toplam ayrılan", 1000),
            Sample("01:00:05", "BELLEK", "toplam ayrılan", 1400),
            Sample("01:00:10", "BELLEK", "toplam ayrılan", 2500));

        var delta = Assert.Single(analysis.Windows[0].Deltas);
        Assert.Equal(1000d, delta.First);
        Assert.Equal(2500d, delta.Last);
        Assert.Equal(1500d, delta.Delta);
    }

    [Fact]
    public void BoundarySampleClosesOneWindowAndOpensTheNext()
    {
        var analysis = Analyze(
            Sample("01:00:00", "BELLEK", "toplam ayrılan", 100),
            Event("01:00:10", "uzlaştırma bitti"),
            Sample("01:00:10", "BELLEK", "toplam ayrılan", 900),
            Sample("01:00:20", "BELLEK", "toplam ayrılan", 950));

        Assert.Equal(800d, Assert.Single(analysis.Windows[0].Deltas).Delta);
        Assert.Equal(50d, Assert.Single(analysis.Windows[1].Deltas).Delta);
    }

    [Fact]
    public void SkipsMetricWithSingleSampleInWindow()
    {
        var analysis = Analyze(
            Sample("01:00:00", "SÜREÇ", "working set", 100),
            Sample("01:00:05", "SÜREÇ", "working set", 120),
            Sample("01:00:05", "ARAMA", "son süre", 4));

        var labels = analysis.Windows[0].Deltas.Select(delta => delta.Label).ToArray();
        Assert.Equal(["working set"], labels);
    }

    [Fact]
    public void OrdersDeltasByAbsoluteMagnitude()
    {
        var analysis = Analyze(
            Sample("01:00:00", "SÜREÇ", "handle", 100),
            Sample("01:00:00", "BELLEK", "yığın", 100),
            Sample("01:00:05", "SÜREÇ", "handle", 90),
            Sample("01:00:05", "BELLEK", "yığın", 400));

        Assert.Equal("yığın", analysis.Windows[0].Deltas[0].Label);
        Assert.Equal("handle", analysis.Windows[0].Deltas[1].Label);
    }

    [Fact]
    public void NegativeDeltaOutranksSmallerPositiveDelta()
    {
        var analysis = Analyze(
            Sample("01:00:00", "SÜREÇ", "handle", 100),
            Sample("01:00:00", "BELLEK", "yığın", 500),
            Sample("01:00:05", "SÜREÇ", "handle", 140),
            Sample("01:00:05", "BELLEK", "yığın", 100));

        Assert.Equal("yığın", analysis.Windows[0].Deltas[0].Label);
        Assert.Equal(-400d, analysis.Windows[0].Deltas[0].Delta);
    }

    [Fact]
    public void CountsDistinctMetricsAcrossGroups()
    {
        var analysis = Analyze(
            Sample("01:00:00", "SÜREÇ", "handle", 1),
            Sample("01:00:00", "BELLEK", "handle", 1),
            Sample("01:00:05", "SÜREÇ", "handle", 2));

        Assert.Equal(2, analysis.MetricCount);
    }

    [Fact]
    public void EventRowsAreNotCountedAsSamples()
    {
        var analysis = Analyze(
            Sample("01:00:00", "SÜREÇ", "handle", 1),
            Event("01:00:05", "arama"));

        Assert.Equal(2, analysis.RowCount);
        Assert.Equal(1, analysis.SampleRowCount);
    }

    [Fact]
    public void PropagatesSkippedLineCount()
    {
        var parsed = MetricLogParser.Parse(
        [
            Sample("01:00:00", "SÜREÇ", "handle", 1),
            "2026-08-27 01:00:05.000;SÜREÇ;handle;bozuk"
        ]);

        var analysis = MetricLogAnalyzer.Analyze(parsed);

        Assert.Equal(1, analysis.SkippedLines);
    }

    private static MetricLogAnalysis Analyze(params string[] lines) =>
        MetricLogAnalyzer.Analyze(MetricLogParser.Parse(lines));

    private static string Sample(string time, string group, string label, double numeric) =>
        $"2026-08-27 {time}.000;{group};{label};{numeric};{numeric}";

    private static string Event(string time, string name) =>
        $"2026-08-27 {time}.000;OLAY;{name};;";
}
