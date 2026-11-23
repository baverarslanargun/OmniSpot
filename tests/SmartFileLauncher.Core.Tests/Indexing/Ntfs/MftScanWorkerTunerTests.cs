using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class MftScanWorkerTunerTests
{
    [Theory]
    [InlineData(0, 1)]
    [InlineData(1, 1)]
    [InlineData(4, 4)]
    [InlineData(5, 4)]
    [InlineData(7, 6)]
    [InlineData(16, 16)]
    [InlineData(99, 16)]
    public void StartIsRoundedDownToAKnownWorkerCount(int start, int expected)
    {
        var tuner = new MftScanWorkerTuner(start);

        Assert.Equal(expected, tuner.Target);
        Assert.Equal(expected, tuner.Anchor);
    }

    [Fact]
    public void StaysAtTheStartWhenNeighboursAreNotFaster()
    {
        var tuner = new MftScanWorkerTuner(4);

        var time = Simulate(tuner, 30, (workers, _) => workers switch { 4 => 800, 6 => 780, 3 => 760, _ => 400 });

        Assert.Equal(4, tuner.Anchor);
        Assert.Subset(new HashSet<int> { 3, 4, 6 }, time.Keys.ToHashSet());
        Assert.True(time[4] / time.Values.Sum() >= 0.7);
    }

    [Fact]
    public void EqualRatesNeverMoveTheWorkerCount()
    {
        var tuner = new MftScanWorkerTuner(4);

        Simulate(tuner, 60, (_, _) => 800);

        Assert.Equal(4, tuner.Anchor);
    }

    [Fact]
    public void SlowFirstChunkIsNotCountedAsAMeasurement()
    {
        var tuner = new MftScanWorkerTuner(4);

        var time = Simulate(tuner, 10, (_, _) => 800, firstChunkDelay: 3.4);

        Assert.Equal(4, tuner.Anchor);
        Assert.Subset(new HashSet<int> { 3, 4, 6 }, time.Keys.ToHashSet());
    }

    [Fact]
    public void ClimbsWhileMoreWorkersKeepPayingOff()
    {
        var tuner = new MftScanWorkerTuner(4);

        Simulate(tuner, 20, (workers, _) => 200 * Math.Min(workers, 12));

        Assert.Equal(12, tuner.Anchor);
    }

    [Fact]
    public void DescendsWhenFewerWorkersAreFaster()
    {
        var tuner = new MftScanWorkerTuner(4);

        Simulate(tuner, 20, (workers, _) => 800.0 / workers);

        Assert.Equal(1, tuner.Anchor);
    }

    [Fact]
    public void KeepsMeasuringAfterItHasSettled()
    {
        var tuner = new MftScanWorkerTuner(4);

        Simulate(tuner, 40, (workers, _) => workers == 4 ? 800 : 700);
        Assert.Equal(4, tuner.Anchor);
        Simulate(tuner, 100, (workers, _) => 800.0 / workers, startSeconds: 40);

        Assert.Equal(1, tuner.Anchor);
    }

    private static Dictionary<int, double> Simulate(
        MftScanWorkerTuner tuner, double untilSeconds, Func<int, double, double> chunksPerSecond,
        double firstChunkDelay = 0, double startSeconds = 0)
    {
        var time = new Dictionary<int, double>();
        var clock = startSeconds + firstChunkDelay;
        var committed = (long)(startSeconds * 1000);
        while (clock < untilSeconds)
        {
            var workers = tuner.Target;
            var step = 1 / chunksPerSecond(workers, clock);
            clock += step;
            committed++;
            time[workers] = time.GetValueOrDefault(workers) + step;
            tuner.Observe(TimeSpan.FromSeconds(clock), committed);
        }
        return time;
    }
}
