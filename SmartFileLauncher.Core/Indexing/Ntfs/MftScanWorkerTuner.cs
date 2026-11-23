namespace SmartFileLauncher.Core.Indexing.Ntfs;

internal sealed class MftScanWorkerTuner
{
    private const int MinimumWindowChunks = 32;
    private const int SettleChunks = 32;
    private const double RequiredGain = 1.05;
    private const int FirstHoldWindows = 4;
    private const int LongestHoldWindows = 32;
    private static readonly int[] Levels = [1, 2, 3, 4, 6, 8, 12, 16];
    private static readonly TimeSpan MinimumWindow = TimeSpan.FromMilliseconds(400);
    private static readonly TimeSpan SettleTime = TimeSpan.FromMilliseconds(100);

    private enum Step { Lead, FirstProbe, SecondProbe, Trail, Hold }

    private Step _step = Step.Lead;
    private int _anchor;
    private int _target;
    private int _direction = 1;
    private int _rejected;
    private int _holdWindows = FirstHoldWindows;
    private int _holdLeft;
    private double _anchorRate;
    private double _probeRate;
    private bool _windowOpen;
    private TimeSpan _windowStart;
    private long _windowStartChunks;
    private TimeSpan _settleUntil;
    private long _settleUntilChunks = 1;

    internal MftScanWorkerTuner(int startWorkers)
    {
        _anchor = LevelFor(startWorkers);
        _target = _anchor;
    }

    internal static int DefaultStartWorkers => Math.Clamp(Environment.ProcessorCount / 2, 1, 4);

    internal int Target => Levels[_target];

    internal int Anchor => Levels[_anchor];

    internal int Observe(TimeSpan elapsed, long committedChunks)
    {
        if (!_windowOpen)
        {
            if (committedChunks < _settleUntilChunks || elapsed < _settleUntil)
                return Target;
            _windowOpen = true;
            _windowStart = elapsed;
            _windowStartChunks = committedChunks;
            return Target;
        }

        var chunks = committedChunks - _windowStartChunks;
        var span = elapsed - _windowStart;
        if (chunks < MinimumWindowChunks || span < MinimumWindow)
            return Target;

        var previous = _target;
        Advance(chunks / span.TotalSeconds);
        if (_target == previous)
        {
            _windowStart = elapsed;
            _windowStartChunks = committedChunks;
        }
        else
        {
            _windowOpen = false;
            _settleUntil = elapsed + SettleTime;
            _settleUntilChunks = committedChunks + SettleChunks;
        }
        return Target;
    }

    private void Advance(double rate)
    {
        switch (_step)
        {
            case Step.Lead:
                _anchorRate = rate;
                BeginProbe();
                break;
            case Step.FirstProbe:
                _probeRate = rate;
                _step = Step.SecondProbe;
                break;
            case Step.SecondProbe:
                _probeRate += rate;
                _target = _anchor;
                _step = Step.Trail;
                break;
            case Step.Trail:
                if (_probeRate > (_anchorRate + rate) * RequiredGain)
                {
                    _anchor += _direction;
                    _target = _anchor;
                    _rejected = 0;
                    _holdWindows = FirstHoldWindows;
                    _step = Step.Lead;
                    break;
                }
                _rejected++;
                _direction = -_direction;
                _anchorRate = rate;
                BeginProbe();
                break;
            case Step.Hold:
                if (--_holdLeft > 0)
                    break;
                _anchorRate = rate;
                BeginProbe();
                break;
        }
    }

    private void BeginProbe()
    {
        while (_rejected < 2)
        {
            var neighbor = _anchor + _direction;
            if (neighbor >= 0 && neighbor < Levels.Length)
            {
                _target = neighbor;
                _step = Step.FirstProbe;
                return;
            }
            _rejected++;
            _direction = -_direction;
        }

        _rejected = 0;
        _holdLeft = _holdWindows;
        _holdWindows = Math.Min(LongestHoldWindows, _holdWindows * 2);
        _target = _anchor;
        _step = Step.Hold;
    }

    private static int LevelFor(int workers)
    {
        var level = 0;
        for (var index = 1; index < Levels.Length; index++)
        {
            if (Levels[index] <= workers)
                level = index;
        }
        return level;
    }
}
