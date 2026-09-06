using System.Globalization;

namespace SmartFileLauncher.Core.ChangeFeed;

public readonly record struct ChangeFeedWatcherLease(long ExpiresAtUtcTicks)
{
    public static ChangeFeedWatcherLease None => new(0);

    public static TimeSpan MaximumDuration => TimeSpan.FromMinutes(10);

    public bool IsHeld(DateTime utcNow)
    {
        if (ExpiresAtUtcTicks <= 0)
        {
            return false;
        }

        var remaining = ExpiresAtUtcTicks - utcNow.Ticks;
        return remaining > 0 && remaining <= MaximumDuration.Ticks;
    }

    public static ChangeFeedWatcherLease Until(DateTime utcNow, TimeSpan duration)
    {
        if (duration <= TimeSpan.Zero)
        {
            return None;
        }

        var capped = duration > MaximumDuration ? MaximumDuration : duration;
        return new ChangeFeedWatcherLease(utcNow.Add(capped).Ticks);
    }

    public string ToPersistedValue() =>
        ExpiresAtUtcTicks.ToString(CultureInfo.InvariantCulture);

    public static ChangeFeedWatcherLease FromPersistedValue(string? value) =>
        long.TryParse(
            value,
            NumberStyles.None,
            CultureInfo.InvariantCulture,
            out var ticks)
            ? new ChangeFeedWatcherLease(ticks)
            : None;
}
