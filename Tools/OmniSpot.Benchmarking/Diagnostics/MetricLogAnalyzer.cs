namespace OmniSpot.Benchmarking.Diagnostics;

internal sealed record MetricDelta(
    string Group,
    string Label,
    double First,
    double Last,
    string FirstText,
    string LastText,
    int SampleCount)
{
    public double Delta => Last - First;
}

internal sealed record MetricWindow(
    string StartLabel,
    DateTime StartAt,
    string EndLabel,
    DateTime EndAt,
    int SampleCount,
    IReadOnlyList<MetricDelta> Deltas)
{
    public TimeSpan Duration => EndAt - StartAt;
}

internal sealed record MetricLogAnalysis(
    DateTime FirstAt,
    DateTime LastAt,
    int RowCount,
    int SampleRowCount,
    int MetricCount,
    int SkippedLines,
    IReadOnlyList<MetricLogRow> Events,
    IReadOnlyList<MetricWindow> Windows)
{
    public TimeSpan Duration => LastAt - FirstAt;
}

internal static class MetricLogAnalyzer
{
    public const string FileStartLabel = "dosya başı";
    public const string FileEndLabel = "dosya sonu";

    public static MetricLogAnalysis Analyze(MetricLogParseResult parsed)
    {
        ArgumentNullException.ThrowIfNull(parsed);

        var rows = parsed.Rows;
        if (rows.Count == 0)
        {
            return new MetricLogAnalysis(
                default,
                default,
                0,
                0,
                0,
                parsed.SkippedLines,
                Array.Empty<MetricLogRow>(),
                Array.Empty<MetricWindow>());
        }

        var events = rows.Where(row => row.IsEvent).ToArray();
        var samples = rows.Where(row => !row.IsEvent).ToArray();
        var metricCount = samples
            .Select(row => (row.Group, row.Label))
            .Distinct()
            .Count();

        var boundaries = new List<(string Label, DateTime At)>
        {
            (FileStartLabel, rows[0].Timestamp)
        };
        boundaries.AddRange(events.Select(row => (Label: DescribeEvent(row), At: row.Timestamp)));
        boundaries.Add((FileEndLabel, rows[^1].Timestamp));

        var windows = new List<MetricWindow>();
        for (var index = 0; index < boundaries.Count - 1; index++)
        {
            var start = boundaries[index];
            var end = boundaries[index + 1];
            var slice = samples
                .Where(row => row.Timestamp >= start.At && row.Timestamp <= end.At)
                .ToArray();

            windows.Add(
                new MetricWindow(
                    start.Label,
                    start.At,
                    end.Label,
                    end.At,
                    slice.Length,
                    BuildDeltas(slice)));
        }

        return new MetricLogAnalysis(
            rows[0].Timestamp,
            rows[^1].Timestamp,
            rows.Count,
            samples.Length,
            metricCount,
            parsed.SkippedLines,
            events,
            windows);
    }

    private static IReadOnlyList<MetricDelta> BuildDeltas(IReadOnlyList<MetricLogRow> slice)
    {
        var deltas = new List<MetricDelta>();

        foreach (var group in slice.Where(row => row.Numeric.HasValue)
                     .GroupBy(row => (row.Group, row.Label)))
        {
            var ordered = group.OrderBy(row => row.Timestamp).ToArray();
            if (ordered.Length < 2) continue;

            deltas.Add(
                new MetricDelta(
                    group.Key.Group,
                    group.Key.Label,
                    ordered[0].Numeric!.Value,
                    ordered[^1].Numeric!.Value,
                    ordered[0].Value,
                    ordered[^1].Value,
                    ordered.Length));
        }

        deltas.Sort((left, right) => Math.Abs(right.Delta).CompareTo(Math.Abs(left.Delta)));
        return deltas;
    }

    private static string DescribeEvent(MetricLogRow row)
    {
        return string.IsNullOrWhiteSpace(row.Value)
            ? row.Label
            : $"{row.Label} ({row.Value})";
    }
}
