using SmartFileLauncher.Core.Indexing.Ntfs;

namespace OmniSpot.Benchmarking.Ntfs;

internal sealed record MftSimScenario(
    string Name, string DeviceClass, double[] GateMiBPerSecond, double MftMiB, double ParseMsPerMiB, int Cores,
    double ConsumerMiBPerSecond = 0, double LoadFromSeconds = -1, double LoadToSeconds = -1, double LoadFactor = 1,
    double Noise = 0.15, double StartupMs = 220, double[]? ParseProfile = null, double ParseContention = 0.6);

internal sealed record MftSimOutcome(
    string Controller, double Seconds, int FinalReaders, int FinalWorkers, int Changes, double WorstGapMs,
    double WorkerSeconds);

internal interface IMftSimController
{
    int Readers { get; }
    int Workers { get; }
    void OnRead(double latencyMs, int active);
    void OnCommit(double elapsedSeconds, long committed, double busySeconds, double gateWaitSeconds, double windowWaitSeconds);
}

internal static class MftPipelineSim
{
    private const int Window = 32;
    private enum Kind { ReadDone, ParseDone, Commit }
    private readonly record struct Event(Kind Kind, int Worker, long Ordinal);

    internal static MftSimOutcome Run(MftSimScenario scenario, IMftSimController controller, string name, int seed)
    {
        var random = new Random(seed);
        var chunks = (long)Math.Ceiling(scenario.MftMiB);
        var queue = new PriorityQueue<Event, double>();
        var maxWorkers = 64;
        var state = new int[maxWorkers];
        var ordinalOf = new long[maxWorkers];
        var readStart = new double[maxWorkers];
        var activeAtStart = new int[maxWorkers];
        var parsed = new HashSet<long>();
        long claimed = 0, nextCommit = 0, committed = 0;
        int reading = 0, parsing = 0, inFlight = 0, changes = 0;
        double now = 0, consumerFreeAt = 0, busy = 0, lastCommit = 0, worstGap = 0, workerSeconds = 0, lastAccount = 0;
        double gateWait = 0, windowWait = 0;
        int gateBlocked = 0, windowBlocked = 0;
        var commitScheduled = false;
        var first = true;
        var lastReaders = controller.Readers;
        var lastWorkers = controller.Workers;

        double Noise() => Math.Exp(scenario.Noise * Gaussian(random)) * (random.NextDouble() < 0.1 ? 1.6 : 1.0);
        double Gate(int active)
        {
            var curve = scenario.GateMiBPerSecond;
            var value = curve[Math.Min(active, curve.Length) - 1];
            if (now >= scenario.LoadFromSeconds && now < scenario.LoadToSeconds)
                value *= scenario.LoadFactor;
            return value;
        }
        double ParseSeconds(long ordinal)
        {
            var profile = scenario.ParseProfile;
            var factor = profile is null ? 1 : profile[(int)Math.Min(profile.Length - 1, ordinal * profile.Length / chunks)];
            var contention = 1 + scenario.ParseContention * (parsing - 1);
            var crowd = Math.Max(1.0, (parsing + 1.0) / Math.Max(1, scenario.Cores));
            return scenario.ParseMsPerMiB / 1000 * factor * contention * crowd * Noise();
        }
        void Account()
        {
            workerSeconds += (now - lastAccount) * Math.Min(controller.Workers, maxWorkers);
            gateWait += (now - lastAccount) * gateBlocked;
            windowWait += (now - lastAccount) * windowBlocked;
            lastAccount = now;
        }
        void Dispatch()
        {
            gateBlocked = 0;
            windowBlocked = 0;
            for (var worker = 0; worker < Math.Min(controller.Workers, maxWorkers); worker++)
            {
                if (state[worker] != 0 || claimed >= chunks)
                    continue;
                if (inFlight >= Window)
                {
                    windowBlocked++;
                    continue;
                }
                if (reading >= controller.Readers)
                {
                    gateBlocked++;
                    continue;
                }
                state[worker] = 1;
                ordinalOf[worker] = claimed++;
                inFlight++;
                reading++;
                readStart[worker] = now;
                activeAtStart[worker] = reading;
                var seconds = reading / Gate(reading) * Noise();
                if (first)
                {
                    seconds += scenario.StartupMs / 1000;
                    first = false;
                }
                queue.Enqueue(new Event(Kind.ReadDone, worker, ordinalOf[worker]), now + seconds);
            }
        }
        void ScheduleCommit()
        {
            if (commitScheduled || !parsed.Contains(nextCommit))
                return;
            commitScheduled = true;
            var cost = scenario.ConsumerMiBPerSecond > 0 ? 1 / scenario.ConsumerMiBPerSecond : 0;
            consumerFreeAt = Math.Max(now, consumerFreeAt) + cost;
            queue.Enqueue(new Event(Kind.Commit, -1, nextCommit), consumerFreeAt);
        }

        Dispatch();
        while (committed < chunks && queue.TryDequeue(out var item, out var time))
        {
            now = time;
            Account();
            switch (item.Kind)
            {
                case Kind.ReadDone:
                    reading--;
                    var latency = now - readStart[item.Worker];
                    busy += latency;
                    controller.OnRead(latency * 1000, activeAtStart[item.Worker]);
                    state[item.Worker] = 2;
                    parsing++;
                    var parse = ParseSeconds(item.Ordinal);
                    busy += parse;
                    queue.Enqueue(new Event(Kind.ParseDone, item.Worker, item.Ordinal), now + parse);
                    break;
                case Kind.ParseDone:
                    parsing--;
                    state[item.Worker] = 0;
                    parsed.Add(item.Ordinal);
                    ScheduleCommit();
                    break;
                case Kind.Commit:
                    commitScheduled = false;
                    parsed.Remove(item.Ordinal);
                    nextCommit++;
                    committed++;
                    inFlight--;
                    if (committed > 1)
                        worstGap = Math.Max(worstGap, now - lastCommit);
                    lastCommit = now;
                    controller.OnCommit(now, committed, busy, gateWait, windowWait);
                    ScheduleCommit();
                    break;
            }
            if (controller.Readers != lastReaders || controller.Workers != lastWorkers)
            {
                changes++;
                lastReaders = controller.Readers;
                lastWorkers = controller.Workers;
            }
            Dispatch();
        }
        return new MftSimOutcome(name, Math.Round(now, 3), controller.Readers, controller.Workers, changes,
            Math.Round(worstGap * 1000, 1), Math.Round(workerSeconds, 2));
    }

    private static double Gaussian(Random random)
    {
        var u = 1.0 - random.NextDouble();
        var v = random.NextDouble();
        return Math.Sqrt(-2 * Math.Log(u)) * Math.Cos(2 * Math.PI * v);
    }

    internal sealed class Fixed(int readers, int workers) : IMftSimController
    {
        public int Readers => readers;
        public int Workers => workers;
        public void OnRead(double latencyMs, int active) { }
        public void OnCommit(double elapsedSeconds, long committed, double busySeconds, double gateWaitSeconds, double windowWaitSeconds) { }
    }

    internal sealed class Proposed : IMftSimController
    {
        private readonly ReadGateController _gate;
        private readonly WorkerDemandController _workers;
        private double _lastBusy;
        private double _lastTime;

        internal Proposed(int startReaders, int maxReaders, int startWorkers, int maxWorkers, double beta)
        {
            _gate = new ReadGateController(startReaders, maxReaders, beta: beta);
            _workers = new WorkerDemandController(startWorkers, maxWorkers);
        }

        public int Readers => _gate.Limit;
        public int Workers => _workers.Workers;
        public void OnRead(double latencyMs, int active) => _gate.Observe(latencyMs, active);

        public void OnCommit(double elapsedSeconds, long committed, double busySeconds, double gateWaitSeconds, double windowWaitSeconds)
        {
            if (committed % 64 != 0)
                return;
            _workers.Observe(busySeconds - _lastBusy, elapsedSeconds - _lastTime);
            _lastBusy = busySeconds;
            _lastTime = elapsedSeconds;
        }
    }

    internal sealed class Queued : IMftSimController
    {
        private readonly ReadGateRamp _gate;
        private readonly WorkerQueueController _workers;
        private double _lastGate;
        private double _lastWindow;
        private double _lastTime;

        internal Queued(int startReaders, int maxReaders, int startWorkers, int maxWorkers)
        {
            _gate = new ReadGateRamp(startReaders, maxReaders);
            _workers = new WorkerQueueController(startWorkers, maxWorkers);
        }

        public int Readers => _gate.Limit;
        public int Workers => _workers.Workers;
        public void OnRead(double latencyMs, int active) => _gate.Observe(latencyMs, active);

        public void OnCommit(double elapsedSeconds, long committed, double busySeconds, double gateWaitSeconds, double windowWaitSeconds)
        {
            if (committed % 64 != 0)
                return;
            _workers.Observe(gateWaitSeconds - _lastGate, windowWaitSeconds - _lastWindow, elapsedSeconds - _lastTime);
            _lastGate = gateWaitSeconds;
            _lastWindow = windowWaitSeconds;
            _lastTime = elapsedSeconds;
        }
    }

    internal sealed class CurrentTuner(int start) : IMftSimController
    {
        private readonly MftScanWorkerTuner _tuner = new(start);
        public int Readers => _tuner.Target;
        public int Workers => _tuner.Target;
        public void OnRead(double latencyMs, int active) { }

        public void OnCommit(double elapsedSeconds, long committed, double busySeconds, double gateWaitSeconds, double windowWaitSeconds) =>
            _tuner.Observe(TimeSpan.FromSeconds(elapsedSeconds), committed);
    }

    internal sealed class OldDoubling : IMftSimController
    {
        private double _lastDecision;
        private long _lastCommitted;
        private double _bestRate;
        private int _bestWorkers = 1;
        private bool _settled;
        private int _target = 1;

        public int Readers => _target;
        public int Workers => _target;
        public void OnRead(double latencyMs, int active) { }

        public void OnCommit(double elapsedSeconds, long committed, double busySeconds, double gateWaitSeconds, double windowWaitSeconds)
        {
            if (elapsedSeconds - _lastDecision < 1.2)
                return;
            var rate = (committed - _lastCommitted) / Math.Max(0.001, elapsedSeconds - _lastDecision);
            _lastCommitted = committed;
            _lastDecision = elapsedSeconds;
            if (rate > _bestRate * 1.03)
            {
                _bestRate = rate;
                _bestWorkers = _target;
            }
            if (!_settled && _target > _bestWorkers && rate <= _bestRate * 1.03)
            {
                _target = _bestWorkers;
                _settled = true;
            }
            else if (!_settled && _target < 16)
            {
                _target = Math.Min(16, _target * 2);
            }
        }
    }
}
