using System.Globalization;
using System.Text;

namespace OmniSpot.Benchmarking.Diagnostics;

internal static class MetricLogSummaryFormatter
{
    public static string Format(
        string filePath,
        MetricLogAnalysis analysis,
        int topCount)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        ArgumentNullException.ThrowIfNull(analysis);

        var builder = new StringBuilder();
        builder.AppendLine("=== OmniSpot tanılama sayaç özeti ===");
        builder.AppendLine($"dosya         : {Path.GetFileName(filePath)}");

        if (analysis.RowCount == 0)
        {
            builder.AppendLine("satır         : 0 — okunabilir sayaç satırı yok");
            AppendSkipped(builder, analysis);
            return builder.ToString();
        }

        builder.AppendLine(
            $"kapsam        : {Stamp(analysis.FirstAt)} → {Stamp(analysis.LastAt)} "
            + $"({Duration(analysis.Duration)})");
        builder.AppendLine(
            $"satır         : {analysis.RowCount:N0} "
            + $"(örnek {analysis.SampleRowCount:N0} · olay {analysis.Events.Count:N0})");
        builder.AppendLine($"metrik        : {analysis.MetricCount:N0}");
        AppendSkipped(builder, analysis);

        builder.AppendLine();
        builder.AppendLine("--- OLAY zinciri ---");
        if (analysis.Events.Count == 0)
        {
            builder.AppendLine("  (olay işaretçisi yok — pencere ayrımı yapılamıyor)");
        }
        else
        {
            var index = 1;
            foreach (var evt in analysis.Events)
            {
                var detail = string.IsNullOrWhiteSpace(evt.Value) ? string.Empty : $" · {evt.Value}";
                builder.AppendLine(
                    $"  {index,3}  {evt.Timestamp:HH:mm:ss}  {evt.Label}{detail}");
                index++;
            }
        }

        for (var index = 0; index < analysis.Windows.Count; index++)
        {
            var window = analysis.Windows[index];
            builder.AppendLine();
            builder.AppendLine(
                $"--- Pencere {index + 1}: {window.StartLabel} → {window.EndLabel} ---");
            builder.AppendLine(
                $"    {Stamp(window.StartAt)} → {Stamp(window.EndAt)} · "
                + $"{Duration(window.Duration)} · {window.SampleCount:N0} örnek satırı");

            var moved = window.Deltas.Where(delta => delta.Delta != 0d).Take(topCount).ToArray();
            if (moved.Length == 0)
            {
                builder.AppendLine("    (bu pencerede değişen sayısal metrik yok)");
                continue;
            }

            var groupWidth = moved.Max(delta => delta.Group.Length);
            var labelWidth = moved.Max(delta => delta.Label.Length);
            var textWidth = moved.Max(delta => delta.FirstText.Length);

            foreach (var delta in moved)
            {
                builder.AppendLine(
                    $"    {delta.Group.PadRight(groupWidth)}  "
                    + $"{delta.Label.PadRight(labelWidth)}  "
                    + $"{delta.FirstText.PadLeft(textWidth)} → {delta.LastText}"
                    + $"   Δ {Signed(delta.Delta)}");
            }
        }

        return builder.ToString();
    }

    private static void AppendSkipped(StringBuilder builder, MetricLogAnalysis analysis)
    {
        var marker = analysis.SkippedLines > 0 ? "  ← İNCELE" : string.Empty;
        builder.AppendLine($"atlanan satır : {analysis.SkippedLines:N0}{marker}");
    }

    private static string Stamp(DateTime value) =>
        value.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);

    private static string Duration(TimeSpan value)
    {
        if (value < TimeSpan.Zero) value = TimeSpan.Zero;
        if (value.TotalSeconds < 60d)
            return $"{value.TotalSeconds.ToString("0.#", CultureInfo.InvariantCulture)} sn";
        if (value.TotalHours < 1d)
            return $"{(int)value.TotalMinutes} dk {value.Seconds} sn";
        return $"{(int)value.TotalHours} sa {value.Minutes} dk";
    }

    private static string Signed(double value)
    {
        var text = value == Math.Floor(value) && Math.Abs(value) < 1e15
            ? Math.Abs(value).ToString("F0", CultureInfo.InvariantCulture)
            : Math.Abs(value).ToString("R", CultureInfo.InvariantCulture);

        return value < 0d ? $"-{text}" : $"+{text}";
    }
}
