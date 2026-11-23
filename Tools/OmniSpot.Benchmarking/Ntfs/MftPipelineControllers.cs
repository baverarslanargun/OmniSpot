namespace OmniSpot.Benchmarking.Ntfs;

internal sealed class ReadGateController
{
    private readonly int _maximum;
    private readonly double _alpha;
    private readonly double _beta;
    private readonly int _samplesPerDecision;
    private readonly int _decisionsPerBaseline;
    private readonly int _baselineMemory;
    private readonly List<double> _saturated = [];
    private readonly Queue<double> _baselines = new();
    private int _unsaturated;
    private int _decisionsSinceBaseline;
    private int _increaseHold;
    private int _resume;

    internal ReadGateController(int start, int maximum, double alpha = 0.6, double beta = 1.5,
        int samplesPerDecision = 32, int decisionsPerBaseline = 40, int baselineMemory = 4)
    {
        _maximum = Math.Max(1, maximum);
        _alpha = alpha;
        _beta = beta;
        _samplesPerDecision = Math.Max(4, samplesPerDecision);
        _decisionsPerBaseline = Math.Max(1, decisionsPerBaseline);
        _baselineMemory = Math.Max(1, baselineMemory);
        _resume = Math.Clamp(start, 1, _maximum);
        Limit = 1;
    }

    internal int Limit { get; private set; }

    internal double Baseline => _baselines.Count == 0 ? double.NaN : _baselines.Min();

    internal double Queue { get; private set; }

    internal bool Observe(double latency, int activeAtStart)
    {
        if (activeAtStart > Limit)
            return false;
        if (activeAtStart == Limit)
            _saturated.Add(latency);
        else
            _unsaturated++;
        if (_saturated.Count + _unsaturated < _samplesPerDecision)
            return false;

        var previous = Limit;
        var saturated = _saturated.Count;
        var level = saturated == 0 ? double.NaN : LowerQuartile(_saturated);
        var idle = _unsaturated;
        _saturated.Clear();
        _unsaturated = 0;

        if (_increaseHold > 0)
            _increaseHold--;
        if (saturated * 4 < idle + saturated)
        {
            if (Limit > 1)
            {
                Limit--;
                _increaseHold = 8;
            }
            return Limit != previous;
        }

        if (Limit == 1)
        {
            if (_resume > 1 || _baselines.Count == 0 || ++_decisionsSinceBaseline >= _decisionsPerBaseline)
            {
                _baselines.Enqueue(level);
                while (_baselines.Count > _baselineMemory)
                    _baselines.Dequeue();
                _decisionsSinceBaseline = 0;
            }
            if (_resume > 1)
            {
                Limit = _resume;
                _resume = 0;
                return true;
            }
        }
        else if (++_decisionsSinceBaseline >= _decisionsPerBaseline)
        {
            _resume = Limit;
            Limit = 1;
            return true;
        }

        if (saturated * 2 < idle + saturated || !(level > 0))
            return false;
        Queue = Limit * (1 - Baseline / level);
        if (Queue < _alpha && Limit < _maximum && _increaseHold == 0)
            Limit++;
        else if (Queue > _beta && Limit > 1)
            Limit--;
        return Limit != previous;
    }

    private static double LowerQuartile(List<double> values)
    {
        var sorted = values.ToArray();
        Array.Sort(sorted);
        return sorted[sorted.Length / 4];
    }
}

internal sealed class ReadGateRamp
{
    private enum Phase { Measure, ProbeUp, ProbeDown, Settled }

    private readonly int _maximum;
    private readonly double _upGain;
    private readonly double _downKeep;
    private readonly int _samples;
    private readonly int _recheckReads;
    private readonly List<double> _window = [];
    private Phase _phase = Phase.Measure;
    private double _rate;
    private int _seen;
    private int _settledReads;
    private bool _movedUp;
    private bool _triedDown;
    private int _recheckAfter;
    private int _parkReads;
    private bool _unusedAbove;

    internal ReadGateRamp(int start, int maximum, double upGain = 1.10, double downKeep = 0.95,
        int samples = 32, int recheckReads = 4000)
    {
        _maximum = Math.Max(1, maximum);
        _upGain = upGain;
        _downKeep = downKeep;
        _samples = Math.Max(4, samples);
        _recheckReads = Math.Max(_samples, recheckReads);
        Limit = Math.Clamp(start, 1, _maximum);
    }

    internal int Limit { get; private set; }

    internal bool Settled => _phase == Phase.Settled;

    internal double Rate => _rate;

    internal bool Observe(double latency, int activeAtStart)
    {
        if (_phase == Phase.Settled)
        {
            if (++_settledReads >= _recheckAfter)
            {
                _phase = Phase.Measure;
                _movedUp = false;
                _triedDown = false;
                Reset();
            }
            return false;
        }
        if (activeAtStart > Limit)
            return false;
        _seen++;
        if (activeAtStart == Limit && latency > 0)
            _window.Add(latency);
        var previous = Limit;
        if (_window.Count < _samples)
        {
            if (_seen < _samples * 4)
                return false;
            Reset();
            if (_phase == Phase.ProbeUp)
            {
                Limit--;
                _unusedAbove = true;
                AfterUpFailed();
            }
            else
            {
                Park();
            }
            return Limit != previous;
        }

        var sorted = _window.ToArray();
        Array.Sort(sorted);
        var rate = Limit / sorted[sorted.Length / 4];
        Reset();
        switch (_phase)
        {
            case Phase.Measure:
                _rate = rate;
                StepUpOrSettle();
                break;
            case Phase.ProbeUp:
                if (rate >= _rate * _upGain)
                {
                    _rate = rate;
                    _movedUp = true;
                    StepUpOrSettle();
                }
                else
                {
                    Limit--;
                    AfterUpFailed();
                }
                break;
            case Phase.ProbeDown:
                if (rate >= _rate * _downKeep)
                {
                    if (Limit > 1)
                        Limit--;
                    else
                        Settle();
                }
                else
                {
                    Limit++;
                    Settle();
                }
                break;
        }
        return Limit != previous;
    }

    private void StepUpOrSettle()
    {
        if (Limit < _maximum)
        {
            Limit++;
            _phase = Phase.ProbeUp;
        }
        else
        {
            AfterUpFailed();
        }
    }

    private void AfterUpFailed()
    {
        if (!_movedUp && !_triedDown && Limit > 1)
        {
            _triedDown = true;
            Limit--;
            _phase = Phase.ProbeDown;
        }
        else
        {
            Settle();
        }
    }

    private void Settle()
    {
        if (_unusedAbove)
        {
            _unusedAbove = false;
            Park();
            return;
        }
        _phase = Phase.Settled;
        _settledReads = 0;
        _recheckAfter = _recheckReads;
        _parkReads = 0;
    }

    private void Park()
    {
        _phase = Phase.Settled;
        _settledReads = 0;
        _parkReads = _parkReads == 0 ? _samples * 8 : Math.Min(_recheckReads, _parkReads * 2);
        _recheckAfter = _parkReads;
    }

    private void Reset()
    {
        _window.Clear();
        _seen = 0;
    }
}

internal sealed class WorkerQueueController
{
    private readonly int _maximum;
    private double _gate = double.NaN;
    private double _window = double.NaN;
    private int _streak;
    private int _last;

    internal WorkerQueueController(int start, int maximum)
    {
        _maximum = Math.Max(1, maximum);
        Workers = Math.Clamp(start, 1, _maximum);
    }

    internal int Workers { get; private set; }

    internal double GateQueue => double.IsNaN(_gate) ? 0 : _gate;

    internal double WindowQueue => double.IsNaN(_window) ? 0 : _window;

    internal bool Observe(double gateWaitSeconds, double windowWaitSeconds, double wallSeconds)
    {
        if (wallSeconds <= 0)
            return false;
        _gate = Smooth(_gate, Math.Max(0, gateWaitSeconds) / wallSeconds);
        _window = Smooth(_window, Math.Max(0, windowWaitSeconds) / wallSeconds);
        var want = _window > 1.0 || _gate > 1.5 ? -1 : _window < 0.1 && _gate < 0.3 ? 1 : 0;
        if (want == 0 || (want > 0 && Workers >= _maximum) || (want < 0 && Workers <= 1))
        {
            _streak = 0;
            _last = 0;
            return false;
        }
        _streak = want == _last ? _streak + 1 : 1;
        _last = want;
        if (_streak < 2)
            return false;
        Workers += want;
        _streak = 0;
        return true;
    }

    private static double Smooth(double current, double sample) =>
        double.IsNaN(current) ? sample : current + 0.5 * (sample - current);
}

internal sealed class WorkerDemandController
{
    private readonly int _maximum;
    private readonly double _targetUtilization;
    private double _smoothed = double.NaN;
    private int _lowStreak;

    internal WorkerDemandController(int start, int maximum, double targetUtilization = 0.85)
    {
        _maximum = Math.Max(1, maximum);
        _targetUtilization = targetUtilization;
        Workers = Math.Clamp(start, 1, _maximum);
    }

    internal int Workers { get; private set; }

    internal double Demand => double.IsNaN(_smoothed) ? 0 : _smoothed;

    internal bool Observe(double busySeconds, double wallSeconds)
    {
        if (wallSeconds <= 0)
            return false;
        var demand = busySeconds / wallSeconds;
        _smoothed = double.IsNaN(_smoothed) ? demand : _smoothed + 0.4 * (demand - _smoothed);
        var wanted = Math.Clamp((int)Math.Ceiling(_smoothed / _targetUtilization), 1, _maximum);
        var previous = Workers;
        if (wanted > Workers)
        {
            Workers = wanted;
            _lowStreak = 0;
        }
        else if (wanted < Workers)
        {
            if (++_lowStreak >= 3)
            {
                Workers--;
                _lowStreak = 0;
            }
        }
        else
        {
            _lowStreak = 0;
        }
        return Workers != previous;
    }
}
