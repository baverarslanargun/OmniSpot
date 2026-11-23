using System.Diagnostics;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

internal readonly record struct MftScanSample(
    double ElapsedMs, int Workers, long Chunks, long Entries,
    double WaitMs, double LongestWaitMs, double DownstreamMs, double LongestDownstreamMs, double GcPauseMs);

internal readonly record struct MftFirstChunk(long Ordinal, double ReadMs, double ParseMs);

internal sealed class MftScanTrace
{
    private static readonly TimeSpan Interval = TimeSpan.FromMilliseconds(200);
    private readonly List<MftScanSample> _samples = [];
    private TimeSpan _lastSample;
    private TimeSpan _lastGcPause = GC.GetTotalPauseDuration();
    private long _entries;
    private long _waitTicks;
    private long _longestWaitTicks;
    private long _downstreamTicks;
    private long _longestDownstreamTicks;

    private readonly System.Collections.Concurrent.ConcurrentQueue<double> _readerOpenMs = new();

    internal IReadOnlyList<MftScanSample> Samples => _samples;

    internal IReadOnlyCollection<double> ReaderOpenMs => _readerOpenMs;

    internal void ReaderOpened(long ticks) => _readerOpenMs.Enqueue(Milliseconds(ticks));

    private readonly System.Collections.Concurrent.ConcurrentQueue<MftFirstChunk> _firstChunks = new();

    internal IReadOnlyCollection<MftFirstChunk> FirstChunks => _firstChunks;

    internal void FirstChunk(long ordinal, long readTicks, long parseTicks) =>
        _firstChunks.Enqueue(new MftFirstChunk(ordinal, Milliseconds(readTicks), Milliseconds(parseTicks)));

    internal void Waited(long ticks)
    {
        _waitTicks += ticks;
        _longestWaitTicks = Math.Max(_longestWaitTicks, ticks);
    }

    internal void Delivered(long ticks, int entries)
    {
        _downstreamTicks += ticks;
        _longestDownstreamTicks = Math.Max(_longestDownstreamTicks, ticks);
        _entries += entries;
    }

    internal void Committed(TimeSpan elapsed, int workers, long chunks, bool last)
    {
        if (!last && elapsed - _lastSample < Interval)
            return;
        var gcPause = GC.GetTotalPauseDuration();
        _samples.Add(new MftScanSample(elapsed.TotalMilliseconds, workers, chunks, _entries,
            Milliseconds(_waitTicks), Milliseconds(_longestWaitTicks),
            Milliseconds(_downstreamTicks), Milliseconds(_longestDownstreamTicks),
            (gcPause - _lastGcPause).TotalMilliseconds));
        _lastSample = elapsed;
        _lastGcPause = gcPause;
        _waitTicks = 0;
        _longestWaitTicks = 0;
        _downstreamTicks = 0;
        _longestDownstreamTicks = 0;
    }

    private static double Milliseconds(long ticks) => ticks * 1000.0 / Stopwatch.Frequency;
}
