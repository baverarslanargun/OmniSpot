using OmniSpot.Benchmarking.Ntfs;
using Xunit;

namespace OmniSpot.Benchmarking.Tests.Ntfs;

public sealed class MftPipelineLabTests
{
    [Theory]
    [InlineData("combined", 3, 1)]
    [InlineData("gated", 4, 2)]
    [InlineData("split", 3, 2)]
    [InlineData("auto-gated", 2, 2)]
    [InlineData("auto-split", 2, 2)]
    public void EveryLayoutDeliversEachChunkOnceAndRespectsTheReadLimit(string mode, int workers, int readers)
    {
        var device = new FakeDevice();
        var options = new MftPipelineLabOptions(mode, workers, readers, ChunkBytes: 4096, Window: 8,
            MaxWorkers: 6, MaxReaders: 3);

        var result = MftPipelineLab.Run(4096 * 300 + 1024, device.Open, options, default);

        Assert.Equal(301, result.Chunks);
        Assert.Equal(4 * 300 + 1, result.Entries);
        Assert.Equal(301, device.Reads);
        Assert.Equal(301, device.Parses);
        Assert.Equal(device.Opened, device.Disposed);
        var limit = mode switch { "combined" => 3, "gated" or "split" => 2, _ => 3 };
        Assert.InRange(device.PeakReads, 1, limit);
        Assert.Equal(301, result.ReadLatency.Sum(row => row.Count));
        Assert.All(result.DecileMs, value => Assert.True(value > 0));
    }

    [Fact]
    public void WorkerFailureStopsTheRunAndJoinsEveryThread()
    {
        var device = new FakeDevice { FailAtPosition = 4096 * 20 };
        var options = new MftPipelineLabOptions("split", 3, 2, ChunkBytes: 4096, Window: 8);

        var error = Assert.Throws<IOException>(() => MftPipelineLab.Run(4096 * 100, device.Open, options, default));

        Assert.IsType<InvalidDataException>(error.InnerException);
        Assert.Equal(device.Opened, device.Disposed);
    }

    [Fact]
    public void SlowConsumerHoldsWorkInFlightWithinTheWindow()
    {
        var device = new FakeDevice();
        var options = new MftPipelineLabOptions("gated", 4, 2, ChunkBytes: 4096, Window: 4,
            ConsumerMicrosecondsPerChunk: 200);

        var result = MftPipelineLab.Run(4096 * 60, device.Open, options, default);

        Assert.Equal(60, result.Chunks);
        Assert.True(result.WallMs >= 60 * 0.2);
        Assert.True(result.WindowWaitMsSum > 0);
    }

    [Fact]
    public void GateStaysLowWhenExtraReadersOnlyQueue()
    {
        var controller = new ReadGateController(start: 4, maximum: 8);
        var limits = Feed(controller, 4000, active => 1.0 * active);

        Assert.Equal(1, limits[0]);
        Assert.All(limits.Skip(200), limit => Assert.InRange(limit, 1, 2));
        Assert.Contains(2, limits);
    }

    [Fact]
    public void GateOpensWhenReadsRunTrulyInParallel()
    {
        var controller = new ReadGateController(start: 2, maximum: 6);
        var limits = Feed(controller, 4000, _ => 1.0);

        Assert.Equal(6, limits.Where(limit => limit > 1).Last());
        Assert.True(limits.Count(limit => limit == 6) > limits.Count / 2);
    }

    [Fact]
    public void GateBacksOffUnderForeignLoadAndReturnsAfterwards()
    {
        var controller = new ReadGateController(start: 4, maximum: 4);
        Feed(controller, 1500, _ => 1.0);
        Assert.Equal(4, controller.Limit);

        var loaded = Feed(controller, 1500, active => 3.0 * active);
        Assert.All(loaded.Skip(300), limit => Assert.Equal(1, limit));

        var recovered = Feed(controller, 3000, _ => 1.0);
        Assert.Equal(4, recovered.Where(limit => limit > 1).Last());
    }

    [Fact]
    public void GateShrinksWhenReadersAreNotTheConstraint()
    {
        var controller = new ReadGateController(start: 4, maximum: 4);
        Feed(controller, 1500, _ => 1.0);
        Assert.Equal(4, controller.Limit);

        for (var index = 0; index < 400; index++)
            controller.Observe(1.0, 1);

        Assert.Equal(1, controller.Limit);
    }

    [Theory]
    [InlineData("auto2-gated")]
    public void QueueDrivenLayoutDeliversEachChunkOnce(string mode)
    {
        var device = new FakeDevice();
        var options = new MftPipelineLabOptions(mode, 2, 2, ChunkBytes: 4096, Window: 8, MaxWorkers: 6, MaxReaders: 3);

        var result = MftPipelineLab.Run(4096 * 300, device.Open, options, default);

        Assert.Equal(300, result.Chunks);
        Assert.Equal(1200, result.Entries);
        Assert.Equal(device.Opened, device.Disposed);
        Assert.InRange(device.PeakReads, 1, 3);
    }

    [Fact]
    public void RampSettlesWhereOneMoreReaderStopsPaying()
    {
        var latency = new Dictionary<int, double> { [1] = 0.51, [2] = 0.65, [3] = 0.90, [4] = 1.21 };
        var ramp = new ReadGateRamp(start: 2, maximum: 4);

        var limits = FeedRamp(ramp, 600, active => latency[active]);

        Assert.Equal(2, ramp.Limit);
        Assert.True(ramp.Settled);
        Assert.Equal(new[] { 2, 3, 1, 2 }, Distinct(limits));

        var later = FeedRamp(ramp, 9000, active => latency[active]);
        Assert.Equal(2, ramp.Limit);
        Assert.True(later.Count(limit => limit == 2) > later.Count * 0.9);
    }

    [Theory]
    [InlineData(1, 2)]
    [InlineData(4, 4)]
    public void RampEndsAtOneReaderWhenTheDeviceServesOneAtATime(int start, int maximum)
    {
        var ramp = new ReadGateRamp(start, maximum);

        FeedRamp(ramp, 1500, active => 6.7 * active);

        Assert.Equal(1, ramp.Limit);
        Assert.True(ramp.Settled);
    }

    [Fact]
    public void RampClimbsWhileEveryExtraReaderPaysAndFollowsALaterChange()
    {
        var ramp = new ReadGateRamp(start: 2, maximum: 4);

        FeedRamp(ramp, 600, _ => 1.0);
        Assert.Equal(4, ramp.Limit);
        Assert.True(ramp.Settled);

        FeedRamp(ramp, 6000, active => 1.0 * active);
        Assert.Equal(1, ramp.Limit);
    }

    [Fact]
    public void RampGivesUpAnExtraReaderThatIsNeverUsed()
    {
        var ramp = new ReadGateRamp(start: 2, maximum: 4);
        for (var index = 0; index < 32; index++)
            ramp.Observe(0.65, 2);
        Assert.Equal(3, ramp.Limit);

        for (var index = 0; index < 128; index++)
            ramp.Observe(0.65, 2);
        Assert.Equal(1, ramp.Limit);
        for (var index = 0; index < 32; index++)
            ramp.Observe(0.51, 1);

        Assert.Equal(2, ramp.Limit);
        Assert.True(ramp.Settled);
    }

    [Fact]
    public void RampStillStepsDownWhenTheExtraReaderIsNeverUsed()
    {
        var ramp = new ReadGateRamp(start: 2, maximum: 4);
        for (var index = 0; index < 32; index++)
            ramp.Observe(13.4, 2);
        for (var index = 0; index < 128; index++)
            ramp.Observe(13.4, 2);
        for (var index = 0; index < 32; index++)
            ramp.Observe(6.7, 1);

        Assert.Equal(1, ramp.Limit);
        Assert.True(ramp.Settled);
    }

    [Fact]
    public void RampRetriesSoonAfterAnExtraReaderCouldNotBeUsed()
    {
        var ramp = new ReadGateRamp(start: 1, maximum: 4);
        for (var index = 0; index < 32; index++)
            ramp.Observe(0.6, 1);
        Assert.Equal(2, ramp.Limit);
        for (var index = 0; index < 128; index++)
            ramp.Observe(0.6, 1);
        Assert.Equal(1, ramp.Limit);

        FeedRamp(ramp, 500, _ => 0.6);

        Assert.Equal(4, ramp.Limit);
        Assert.True(ramp.Settled);
    }

    [Fact]
    public void WorkerCountFollowsTheQueuesInFrontOfTheGateAndTheWindow()
    {
        var controller = new WorkerQueueController(start: 2, maximum: 5);
        for (var index = 0; index < 10; index++)
            controller.Observe(0, 0, 0.1);
        Assert.Equal(5, controller.Workers);

        for (var index = 0; index < 6; index++)
            controller.Observe(0.2, 0, 0.1);
        Assert.Equal(3, controller.Workers);

        for (var index = 0; index < 10; index++)
            controller.Observe(0.08, 0, 0.1);
        Assert.Equal(3, controller.Workers);

        for (var index = 0; index < 10; index++)
            controller.Observe(0, 0.02, 0.1);
        Assert.Equal(3, controller.Workers);

        for (var index = 0; index < 12; index++)
            controller.Observe(0, 0.3, 0.1);
        Assert.Equal(1, controller.Workers);
        Assert.False(controller.Observe(0, 1, 0));
    }

    [Fact]
    public void WorkerCountFollowsMeasuredDemand()
    {
        var controller = new WorkerDemandController(start: 1, maximum: 8);
        for (var index = 0; index < 20; index++)
            controller.Observe(Math.Min(controller.Workers, 3.3) * 0.1, 0.1);
        Assert.Equal(4, controller.Workers);

        for (var index = 0; index < 40; index++)
            controller.Observe(0.9 * 0.1, 0.1);
        Assert.Equal(2, controller.Workers);

        Assert.False(controller.Observe(1, 0));
    }

    private static List<int> FeedRamp(ReadGateRamp ramp, int reads, Func<int, double> latency)
    {
        var limits = new List<int>(reads);
        for (var index = 0; index < reads; index++)
        {
            var active = ramp.Limit;
            ramp.Observe(latency(active), active);
            limits.Add(ramp.Limit);
        }
        return limits;
    }

    private static int[] Distinct(List<int> values)
    {
        var result = new List<int>();
        foreach (var value in values)
        {
            if (result.Count == 0 || result[^1] != value)
                result.Add(value);
        }
        return result.ToArray();
    }

    private static List<int> Feed(ReadGateController controller, int reads, Func<int, double> latency)
    {
        var limits = new List<int>(reads);
        for (var index = 0; index < reads; index++)
        {
            var active = controller.Limit;
            controller.Observe(latency(active), active);
            limits.Add(controller.Limit);
        }
        return limits;
    }

    private sealed class FakeDevice
    {
        private int _activeReads;
        private int _peakReads;
        private int _reads;
        private int _parses;
        private int _opened;
        private int _disposed;

        internal long FailAtPosition { get; init; } = -1;
        internal int PeakReads => Volatile.Read(ref _peakReads);
        internal int Reads => Volatile.Read(ref _reads);
        internal int Parses => Volatile.Read(ref _parses);
        internal int Opened => Volatile.Read(ref _opened);
        internal int Disposed => Volatile.Read(ref _disposed);

        internal IMftPipelineWorker Open()
        {
            Interlocked.Increment(ref _opened);
            return new Worker(this);
        }

        private sealed class Worker(FakeDevice owner) : IMftPipelineWorker
        {
            public void Read(long position, Span<byte> buffer)
            {
                var active = Interlocked.Increment(ref owner._activeReads);
                int peak;
                while (active > (peak = Volatile.Read(ref owner._peakReads)) &&
                       Interlocked.CompareExchange(ref owner._peakReads, active, peak) != peak) { }
                try
                {
                    if (position == owner.FailAtPosition)
                        throw new InvalidDataException("Okuma hatası.");
                    Thread.SpinWait(2000);
                    Interlocked.Increment(ref owner._reads);
                }
                finally { Interlocked.Decrement(ref owner._activeReads); }
            }

            public int Parse(byte[] buffer, long position, int count)
            {
                Thread.SpinWait(3000);
                Interlocked.Increment(ref owner._parses);
                return count / 1024;
            }

            public void Dispose() => Interlocked.Increment(ref owner._disposed);
        }
    }
}
