namespace SmartFileLauncher.UI.Services;

internal sealed class ThumbnailActivityTracker
{
    private readonly Func<TimeSpan> _clock;
    private TimeSpan _lastActivity;

    public ThumbnailActivityTracker(Func<TimeSpan> clock)
    {
        _clock = clock;
        _lastActivity = clock();
    }

    public bool IsIdle { get; private set; }

    public bool RecordActivity()
    {
        var resumed = IsIdle;
        IsIdle = false;
        _lastActivity = _clock();
        return resumed;
    }

    public bool Check(TimeSpan timeout)
    {
        if (IsIdle || _clock() - _lastActivity < timeout) return false;
        IsIdle = true;
        return true;
    }
}
