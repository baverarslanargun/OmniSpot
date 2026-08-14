namespace OmniSpot.Benchmarking.Profiling;

internal readonly record struct BucketRange(
    string Label,
    long Minimum,
    long? Maximum)
{
    internal bool Contains(long value) =>
        value >= Minimum && (Maximum is null || value <= Maximum.Value);
}

internal sealed class FrequencyDistribution
{
    private readonly Dictionary<long, long> _frequencies = new();

    internal long Count { get; private set; }

    internal void Add(long value)
    {
        _frequencies.TryGetValue(value, out var count);
        _frequencies[value] = count + 1;
        Count++;
    }

    internal DistributionSummary Summarize(IReadOnlyList<BucketRange> buckets)
    {
        var histogramCounts = new long[buckets.Count];
        foreach (var (value, count) in _frequencies)
        {
            for (var index = 0; index < buckets.Count; index++)
            {
                if (!buckets[index].Contains(value))
                {
                    continue;
                }

                histogramCounts[index] += count;
                break;
            }
        }

        var histogram = buckets
            .Select((bucket, index) => new HistogramBucket(bucket.Label, histogramCounts[index]))
            .ToArray();

        if (Count == 0)
        {
            return new DistributionSummary(0, 0, 0, 0, 0, 0, histogram);
        }

        var ordered = _frequencies.OrderBy(pair => pair.Key).ToArray();
        return new DistributionSummary(
            Count,
            Percentile(ordered, 0.50),
            Percentile(ordered, 0.90),
            Percentile(ordered, 0.95),
            Percentile(ordered, 0.99),
            ordered[^1].Key,
            histogram);
    }

    private long Percentile(
        IReadOnlyList<KeyValuePair<long, long>> ordered,
        double probability)
    {
        var rank = (long)Math.Ceiling(probability * Count);
        long cumulative = 0;
        foreach (var (value, count) in ordered)
        {
            cumulative += count;
            if (cumulative >= rank)
            {
                return value;
            }
        }

        return ordered[^1].Key;
    }
}

internal static class HistogramDefinitions
{
    internal static IReadOnlyList<BucketRange> Depth { get; } =
    [
        new("0", 0, 0),
        new("1", 1, 1),
        new("2", 2, 2),
        new("3", 3, 3),
        new("4", 4, 4),
        new("5-8", 5, 8),
        new("9-16", 9, 16),
        new("17-32", 17, 32),
        new("33+", 33, null)
    ];

    internal static IReadOnlyList<BucketRange> Count { get; } =
    [
        new("0", 0, 0),
        new("1", 1, 1),
        new("2", 2, 2),
        new("3-4", 3, 4),
        new("5-8", 5, 8),
        new("9-16", 9, 16),
        new("17-32", 17, 32),
        new("33-64", 33, 64),
        new("65-128", 65, 128),
        new("129-256", 129, 256),
        new("257-512", 257, 512),
        new("513-1024", 513, 1024),
        new("1025+", 1025, null)
    ];

    internal static IReadOnlyList<BucketRange> NameLength { get; } =
    [
        new("0", 0, 0),
        new("1-4", 1, 4),
        new("5-8", 5, 8),
        new("9-16", 9, 16),
        new("17-32", 17, 32),
        new("33-64", 33, 64),
        new("65-128", 65, 128),
        new("129-255", 129, 255),
        new("256+", 256, null)
    ];

    internal static IReadOnlyList<BucketRange> TokenFanOut { get; } =
    [
        new("1", 1, 1),
        new("2", 2, 2),
        new("3-4", 3, 4),
        new("5-8", 5, 8),
        new("9-16", 9, 16),
        new("17-32", 17, 32),
        new("33-64", 33, 64),
        new("65-128", 65, 128),
        new("129-256", 129, 256),
        new("257-512", 257, 512),
        new("513-1024", 513, 1024),
        new("1025+", 1025, null)
    ];

    internal static IReadOnlyList<BucketRange> FileSize { get; } =
        CreateFileSizeBuckets();

    private static IReadOnlyList<BucketRange> CreateFileSizeBuckets()
    {
        var buckets = new List<BucketRange>
        {
            new("0", 0, 0)
        };
        for (var exponent = 0; exponent <= 62; exponent++)
        {
            var minimum = 1L << exponent;
            var maximum = exponent == 62
                ? long.MaxValue
                : (1L << (exponent + 1)) - 1;
            buckets.Add(new BucketRange($"2^{exponent}", minimum, maximum));
        }

        return buckets;
    }
}
