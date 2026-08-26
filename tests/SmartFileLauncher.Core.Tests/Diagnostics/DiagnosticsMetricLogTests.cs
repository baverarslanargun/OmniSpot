using SmartFileLauncher.Core.Diagnostics;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Diagnostics;

public class DiagnosticsMetricLogTests
{
    private static readonly DateTime FixedStart = new(2026, 8, 26, 21, 34, 56, DateTimeKind.Local);

    private static string[] ReadLines(string path)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd()
            .Split(Environment.NewLine, StringSplitOptions.RemoveEmptyEntries);
    }

    private static IReadOnlyList<DiagnosticsGroup> Sample(
        params (string Group, string Label, string Value, double? Numeric)[] readings)
    {
        return readings
            .GroupBy(reading => reading.Group)
            .Select(group => new DiagnosticsGroup(
                group.Key,
                group.Select(reading => new DiagnosticsReading(
                        reading.Label,
                        reading.Value,
                        DiagnosticsSeverity.Normal,
                        reading.Numeric))
                    .ToArray()))
            .ToArray();
    }

    [Fact]
    public void StartCreatesMetricFileNamedWithSessionTimestamp()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsMetricLog(() => FixedStart);

        Assert.True(log.Start(directory.Path));

        Assert.Equal(
            Path.Combine(directory.Path, "omnispot-20260826-213456-metrik.csv"),
            log.CurrentFilePath);
        Assert.Equal("zaman;bölüm;etiket;değer;sayısal", ReadLines(log.CurrentFilePath!)[0]);
    }

    [Fact]
    public void SampleWritesOneRowPerReading()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsMetricLog(() => FixedStart);
        log.Start(directory.Path);

        log.WriteSample(Sample(
            ("SÜREÇ", "private", "878,4 MB", 921010176d),
            ("SÜREÇ", "handle", "1.014", 1014d),
            ("İNDEKS", "dosya", "306.543", 306543d)));

        var rows = ReadLines(log.CurrentFilePath!).Skip(1).ToArray();
        Assert.Equal(3, rows.Length);
        Assert.Equal("2026-08-26 21:34:56.000;SÜREÇ;private;878,4 MB;921010176", rows[0]);
        Assert.Equal("2026-08-26 21:34:56.000;İNDEKS;dosya;306.543;306543", rows[2]);
        Assert.Equal(3, log.WrittenRows);
    }

    [Fact]
    public void MissingNumericLeavesTheColumnEmpty()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsMetricLog(() => FixedStart);
        log.Start(directory.Path);

        log.WriteSample(Sample(("KÜÇÜK RESİM", "istenen boyut", "128×128", null)));

        var row = ReadLines(log.CurrentFilePath!).Skip(1).Single();
        Assert.EndsWith(";128×128;", row);
    }

    [Fact]
    public void NumericUsesInvariantDecimalPoint()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsMetricLog(() => FixedStart);
        log.Start(directory.Path);

        log.WriteSample(Sample(("SÜREÇ", "oran", "%12,5", 12.5d)));

        Assert.EndsWith(";12.5", ReadLines(log.CurrentFilePath!).Skip(1).Single());
    }

    [Fact]
    public void EventRowsUseTheEventGroup()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsMetricLog(() => FixedStart);
        log.Start(directory.Path);

        log.WriteEvent("klasör açıldı", "Ekran Görüntüleri", 1000);

        var row = ReadLines(log.CurrentFilePath!).Skip(1).Single();
        Assert.Equal(
            "2026-08-26 21:34:56.000;OLAY;klasör açıldı;Ekran Görüntüleri;1000",
            row);
    }

    [Fact]
    public void SeparatorInsideValuesIsNeutralised()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsMetricLog(() => FixedStart);
        log.Start(directory.Path);

        log.WriteEvent("klasör açıldı", "a;b\r\nc", null);

        var rows = ReadLines(log.CurrentFilePath!).Skip(1).ToArray();
        Assert.Single(rows);
        Assert.Equal(5, rows[0].Split(';').Length);
        Assert.Contains("a,b  c", rows[0]);
    }

    [Fact]
    public void WriteBeforeStartIsIgnored()
    {
        using var log = new DiagnosticsMetricLog(() => FixedStart);

        log.WriteSample(Sample(("SÜREÇ", "private", "1", 1d)));
        log.WriteEvent("olay", "detay");

        Assert.False(log.IsWriting);
        Assert.Equal(0, log.WrittenRows);
    }

    [Fact]
    public void RestartingOpensASecondFile()
    {
        using var directory = new TemporaryDirectory();
        var moment = FixedStart;
        using var log = new DiagnosticsMetricLog(() => moment);

        log.Start(directory.Path);
        var first = log.CurrentFilePath!;
        log.WriteEvent("olay", "birinci");

        moment = FixedStart.AddMinutes(1);
        log.Start(directory.Path);

        Assert.NotEqual(first, log.CurrentFilePath);
        Assert.Equal(0, log.WrittenRows);
        Assert.Equal(2, ReadLines(first).Length);
    }

    [Fact]
    public void HeaderIsNotRepeatedWhenAppendingToAnExistingFile()
    {
        using var directory = new TemporaryDirectory();
        using (var first = new DiagnosticsMetricLog(() => FixedStart))
        {
            first.Start(directory.Path);
            first.WriteEvent("olay", "birinci");
        }

        using var second = new DiagnosticsMetricLog(() => FixedStart);
        second.Start(directory.Path);
        second.WriteEvent("olay", "ikinci");

        var lines = ReadLines(second.CurrentFilePath!);
        Assert.Equal(3, lines.Length);
        Assert.Single(lines, line => line.StartsWith("zaman;"));
    }

    [Fact]
    public void ConcurrentWritersLoseNoRows()
    {
        using var directory = new TemporaryDirectory();
        using var log = new DiagnosticsMetricLog(() => FixedStart);
        log.Start(directory.Path);

        Parallel.For(0, 8, worker =>
        {
            for (var index = 0; index < 50; index++)
            {
                log.WriteEvent("olay", $"w{worker}-{index}");
            }
        });

        var path = log.CurrentFilePath!;
        log.Stop();

        var rows = ReadLines(path).Skip(1).ToArray();
        Assert.Equal(400, rows.Length);
        Assert.All(rows, row => Assert.Equal(5, row.Split(';').Length));
        Assert.Equal(400, log.WrittenRows);
    }
}
