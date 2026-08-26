namespace SmartFileLauncher.Core.Diagnostics;

public sealed class DiagnosticsRateTracker
{
    private static readonly TimeSpan DefaultMinimumInterval = TimeSpan.FromMilliseconds(250);

    private readonly object _sync = new();
    private readonly TimeSpan _minimumInterval;
    private readonly Dictionary<string, Entry> _entries = new(StringComparer.Ordinal);

    public DiagnosticsRateTracker(TimeSpan? minimumInterval = null)
    {
        var interval = minimumInterval ?? DefaultMinimumInterval;
        if (interval <= TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(minimumInterval));

        _minimumInterval = interval;
    }

    public double? Update(string key, double value, DateTime observedAt)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(key);

        lock (_sync)
        {
            if (!_entries.TryGetValue(key, out var previous))
            {
                _entries[key] = new Entry(observedAt, value, null);
                return null;
            }

            if (value < previous.Value || observedAt < previous.ObservedAt)
            {
                _entries[key] = new Entry(observedAt, value, null);
                return null;
            }

            var elapsed = observedAt - previous.ObservedAt;
            if (elapsed < _minimumInterval)
            {
                return previous.Rate;
            }

            var rate = (value - previous.Value) / elapsed.TotalSeconds;
            _entries[key] = new Entry(observedAt, value, rate);
            return rate;
        }
    }

    public void Reset()
    {
        lock (_sync)
        {
            _entries.Clear();
        }
    }

    private readonly record struct Entry(DateTime ObservedAt, double Value, double? Rate);
}
