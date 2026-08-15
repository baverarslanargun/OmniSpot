using System.Globalization;
using System.Text;

namespace OmniSpot.Benchmarking.Measurements;

internal static class MeasurementSummaryFormatter
{
    internal static string Format(PilotDocument pilot)
    {
        var builder = new StringBuilder();
        builder.AppendLine("B-2 pilot özeti");
        builder.AppendLine("  durum: " + (pilot.Regime.Passed ? "geçti" : "geçmedi"));
        builder.AppendLine("  fixture öğesi: " + pilot.Regime.ItemCount.ToString(CultureInfo.InvariantCulture));
        builder.AppendLine("  warmup / ölçüm: " + pilot.Regime.WarmupCount + " / " + pilot.Regime.MeasurementCount);
        builder.AppendLine("  iki koşum varyans bandı: %" + Number(pilot.Regime.TwoRunVarianceBandPercent));
        builder.AppendLine("  regresyon eşiği: %" + Number(pilot.Regime.RegressionThresholdPercent));
        builder.AppendLine("  ölçülebilen en küçük fark: " +
            (pilot.Regime.MinimumDetectableDifferencePercent is double mdd
                ? "%" + Number(mdd)
                : "bulunamadı"));
        builder.AppendLine("  steady-memory idle: " + pilot.Regime.SteadyMemoryIdleSeconds + " sn");
        builder.AppendLine("  günlük önce/sonra izdüşümü: " +
            Number(pilot.Regime.ProjectedDailyComparisonMinutes) + " dk");
        builder.AppendLine("  CPU: " + Frequency(pilot.Environment));
        builder.AppendLine("  karar istatistiği: " + pilot.Regime.DecisionStatistic +
            " | canary tasarımı: " + pilot.Regime.CanaryDesign);
        if (pilot.CanaryPairs.Count > 0)
        {
            builder.AppendLine("  canary merdiveni (eşleştirilmiş fark):");
            foreach (var pair in pilot.CanaryPairs)
            {
                builder.AppendLine(
                    "    %" + Number(pair.CanaryPercent) +
                    " → ölçülen %" + Number(pair.PairedChangePercent) +
                    (pair.Detected ? "  yakalandı" : "  yakalanmadı"));
            }
        }

        if (pilot.AcceptanceFailures.Count > 0)
        {
            builder.AppendLine("  kapanmayan kabul ölçütleri:");
            foreach (var failure in pilot.AcceptanceFailures)
            {
                builder.AppendLine("    - " + failure);
            }
        }

        return builder.ToString();
    }

    internal static string Format(MeasurementDocument document)
    {
        var builder = new StringBuilder();
        builder.AppendLine("B-2 ölçüm özeti");
        builder.AppendLine("  fixture parmak izi: " + document.Fixture.Fingerprint);
        builder.AppendLine("  medyan: " + Number(document.Metrics.MedianNanoseconds / 1_000_000d) + " ms");
        builder.AppendLine("  p95: " + Number(document.Metrics.P95Nanoseconds / 1_000_000d) + " ms");
        builder.AppendLine("  allocation: " +
            (document.Metrics.AllocatedBytesPerOperation is double bytes
                ? Number(bytes / 1_048_576d) + " MiB/op"
                : "ölçülmedi"));
        builder.AppendLine("  BDN duvar süresi: " +
            Number(document.Metrics.Instrumentation.BenchmarkWallMilliseconds / 1_000d) + " sn");
        builder.AppendLine("  CPU: " + Frequency(document.Environment));
        return builder.ToString();
    }

    internal static string Format(ComparisonDocument comparison)
    {
        var builder = new StringBuilder();
        builder.AppendLine("B-2 karşılaştırma özeti");
        builder.AppendLine("  karar: " + Verdict(comparison.Verdict));
        builder.AppendLine("  p95 farkı: %" + Signed(comparison.P95ChangePercent));
        builder.AppendLine("  allocation farkı: " +
            (comparison.AllocationChangePercent is double allocation
                ? "%" + Signed(allocation)
                : "ölçülmedi"));
        builder.AppendLine("  regresyon eşiği: %" + Number(comparison.RegressionThresholdPercent));
        builder.AppendLine("  bu bütçedeki asgari ölçülebilir fark: %" +
            Number(comparison.MinimumDetectableDifferencePercent));
        return builder.ToString();
    }

    private static string Verdict(ComparisonVerdict verdict) => verdict switch
    {
        ComparisonVerdict.Regression => "regresyon",
        ComparisonVerdict.Improvement => "iyileşme",
        ComparisonVerdict.UnmeasurableWithinBudget => "bu bütçede ölçülemez",
        _ => throw new ArgumentOutOfRangeException(nameof(verdict))
    };

    private static string Signed(double value) =>
        value.ToString("+0.00;-0.00;0.00", CultureInfo.InvariantCulture);

    private static string Number(double value) =>
        value.ToString("0.00", CultureInfo.InvariantCulture);

    private static string Optional(int? value) =>
        value?.ToString(CultureInfo.InvariantCulture) ?? "ölçülmedi";

    private static string OptionalPercent(double? value) =>
        value is double measured ? "%" + Number(measured) : "ölçülmedi";

    private static string Frequency(Profiling.ProfileEnvironment environment) =>
        $"AC={Optional(environment.ProcessorThrottleMaxAcStartPercent)}→" +
        $"{Optional(environment.ProcessorThrottleMaxAcEndPercent)}%, " +
        $"DC={Optional(environment.ProcessorThrottleMaxDcStartPercent)}→" +
        $"{Optional(environment.ProcessorThrottleMaxDcEndPercent)}%, " +
        $"yük={Optional(environment.ProcessorFrequencyStartMhz)}→" +
        $"{Optional(environment.ProcessorFrequencyEndMhz)} MHz, " +
        $"kayma={OptionalPercent(environment.ProcessorFrequencyDriftPercent)}, " +
        $"etiket={(environment.Labels.Count == 0 ? "yok" : string.Join(",", environment.Labels))}";
}
