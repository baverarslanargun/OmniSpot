using System.Globalization;
using SmartFileLauncher.Core.Diagnostics;

namespace OmniSpot.Benchmarking.Diagnostics;

internal sealed record MetricLogRow(
    DateTime Timestamp,
    string Group,
    string Label,
    string Value,
    double? Numeric)
{
    public bool IsEvent =>
        string.Equals(Group, DiagnosticsMetricLog.EventGroup, StringComparison.Ordinal);
}

internal sealed record MetricLogParseResult(
    IReadOnlyList<MetricLogRow> Rows,
    int SkippedLines);

internal static class MetricLogParser
{
    private const string TimestampFormat = "yyyy-MM-dd HH:mm:ss.fff";
    private const string HeaderPrefix = "zaman;";
    private const int FieldCount = 5;

    public static MetricLogParseResult Parse(IEnumerable<string> lines)
    {
        ArgumentNullException.ThrowIfNull(lines);

        var rows = new List<MetricLogRow>();
        var skipped = 0;

        foreach (var line in lines)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            if (line.StartsWith(HeaderPrefix, StringComparison.Ordinal)) continue;

            var fields = line.Split(';');
            if (fields.Length != FieldCount)
            {
                skipped++;
                continue;
            }

            if (!DateTime.TryParseExact(
                    fields[0],
                    TimestampFormat,
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out var timestamp))
            {
                skipped++;
                continue;
            }

            double? numeric = null;
            if (fields[4].Length > 0)
            {
                if (!double.TryParse(
                        fields[4],
                        NumberStyles.Float,
                        CultureInfo.InvariantCulture,
                        out var parsed))
                {
                    skipped++;
                    continue;
                }

                numeric = parsed;
            }

            rows.Add(new MetricLogRow(timestamp, fields[1], fields[2], fields[3], numeric));
        }

        rows.Sort((left, right) => left.Timestamp.CompareTo(right.Timestamp));
        return new MetricLogParseResult(rows, skipped);
    }
}
