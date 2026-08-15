namespace OmniSpot.Benchmarking.Measurements;

internal static class MeasurementStatistics
{
    internal static double Median(IReadOnlyList<double> values) => Percentile(values, 50);

    internal static double P95(IReadOnlyList<double> values) => Percentile(values, 95);

    internal static double Percentile(IReadOnlyList<double> values, int percentile)
    {
        if (values.Count == 0)
        {
            throw new ArgumentException("En az bir ölçüm örneği gerekli.", nameof(values));
        }

        if (percentile is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(nameof(percentile));
        }

        var ordered = values.Order().ToArray();
        var rank = (int)Math.Ceiling(percentile / 100d * ordered.Length);
        return ordered[Math.Clamp(rank - 1, 0, ordered.Length - 1)];
    }

    internal static double ChangePercent(double baseline, double candidate)
    {
        if (baseline <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(baseline));
        }

        return (candidate - baseline) / baseline * 100d;
    }
}
