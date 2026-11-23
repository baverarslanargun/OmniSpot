using System.Collections.Concurrent;
using System.Diagnostics;

namespace OmniSpot.Benchmarking.Ntfs;

internal interface IMftPipelineWorker : IDisposable
{
    void Read(long position, Span<byte> buffer);
    int Parse(byte[] buffer, long position, int count);
}

internal sealed record MftPipelineLabOptions(
    string Mode, int Workers, int Readers, int ChunkBytes = 1024 * 1024, int Window = 32,
    int MaxWorkers = 16, int MaxReaders = 8, double ConsumerMicrosecondsPerChunk = 0, bool BackgroundIo = false);

internal sealed record MftPipelineChange(double Ms, long Chunk, int Readers, int Workers, double Queue, double Demand);

internal sealed record MftPipelineLatency(int Active, int Count, double P25Ms, double P50Ms, double P90Ms);

internal sealed class MftPipelineResult
{
    public string Mode { get; set; } = "";
    public int StartWorkers { get; set; }
    public int StartReaders { get; set; }
    public int FinalWorkers { get; set; }
    public int FinalReaders { get; set; }
    public int ChunkBytes { get; set; }
    public int Window { get; set; }
    public double ConsumerMicrosecondsPerChunk { get; set; }
    public long Chunks { get; set; }
    public long Entries { get; set; }
    public double WallMs { get; set; }
    public double FirstChunkMs { get; set; }
    public double MiBPerSecond { get; set; }
    public double ReadMsSum { get; set; }
    public double ParseMsSum { get; set; }
    public double GateWaitMsSum { get; set; }
    public double WindowWaitMsSum { get; set; }
    public double ParserIdleMsSum { get; set; }
    public double ConsumerWaitMs { get; set; }
    public double GcPauseMs { get; set; }
    public double AllocatedMiB { get; set; }
    public double CpuMs { get; set; }
    public double[] DecileMs { get; set; } = [];
    public List<MftPipelineLatency> ReadLatency { get; set; } = [];
    public List<MftPipelineChange> Changes { get; set; } = [];
}

internal static class MftPipelineLab
{
    private readonly record struct ReadSample(long LatencyTicks, int Active);
    private readonly record struct RawChunk(long Ordinal, byte[] Buffer, long Position, int Count);
    private readonly record struct ParsedChunk(long Ordinal, int Entries);

    internal static MftPipelineResult Run(long totalBytes, Func<IMftPipelineWorker> open,
        MftPipelineLabOptions options, CancellationToken cancellationToken)
    {
        var split = options.Mode is "split" or "auto-split";
        var queued = options.Mode == "auto2-gated";
        var auto = queued || options.Mode is "auto-gated" or "auto-split";
        if (!split && !auto && options.Mode is not ("combined" or "gated"))
            throw new ArgumentException("Bilinmeyen hat modu.", nameof(options));
        if (totalBytes <= 0 || options.ChunkBytes <= 0 || options.Window <= 0)
            throw new ArgumentOutOfRangeException(nameof(options));

        var chunkBytes = options.ChunkBytes;
        var chunkCount = (totalBytes + chunkBytes - 1) / chunkBytes;
        var maxWorkers = Math.Max(1, options.MaxWorkers);
        var maxReaders = Math.Max(1, options.MaxReaders);
        var startWorkers = Math.Clamp(options.Workers, 1, maxWorkers);
        var startReaders = options.Mode == "combined" ? maxWorkers : Math.Clamp(options.Readers, 1, maxReaders);
        var workerThreads = auto ? maxWorkers : startWorkers;
        var readerThreads = split ? (auto ? maxReaders : startReaders) : 0;

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        var token = linked.Token;
        using var window = new SemaphoreSlim(options.Window, options.Window);
        using var results = new BlockingCollection<ParsedChunk>();
        using var raw = new BlockingCollection<RawChunk>();
        var pool = new ConcurrentBag<byte[]>();
        var samples = new ConcurrentQueue<ReadSample>();
        var gate = new ReadGate(auto && !queued ? 1 : startReaders);
        var activation = new Activation(startWorkers);
        var gateController = auto && !queued ? new ReadGateController(startReaders, maxReaders) : null;
        var workerController = auto && !queued ? new WorkerDemandController(startWorkers, maxWorkers) : null;
        var gateRamp = queued ? new ReadGateRamp(startReaders, maxReaders) : null;
        var queueController = queued ? new WorkerQueueController(startWorkers, maxWorkers) : null;
        long lastGateWaitTicks = 0, lastWindowWaitTicks = 0;
        Exception? failure = null;
        long claimed = -1;
        long readTicks = 0, parseTicks = 0, gateWaitTicks = 0, windowWaitTicks = 0, parserIdleTicks = 0;
        var inRead = 0;
        var readersAlive = readerThreads;

        void Fail(Exception error)
        {
            Interlocked.CompareExchange(ref failure, error, null);
            linked.Cancel();
            gate.Close();
            activation.Close();
        }

        bool Claim(out long ordinal, out long position, out int count)
        {
            ordinal = -1;
            position = 0;
            count = 0;
            if (!gate.Enter())
                return false;
            var release = true;
            try
            {
                if (Volatile.Read(ref claimed) >= chunkCount - 1)
                    return false;
                var waitStart = Stopwatch.GetTimestamp();
                window.Wait(token);
                Interlocked.Add(ref windowWaitTicks, Stopwatch.GetTimestamp() - waitStart);
                ordinal = Interlocked.Increment(ref claimed);
                if (ordinal >= chunkCount)
                {
                    window.Release();
                    return false;
                }
                position = ordinal * chunkBytes;
                count = (int)Math.Min(chunkBytes, totalBytes - position);
                release = false;
                return true;
            }
            finally
            {
                if (release)
                    gate.Exit();
            }
        }

        void ReadClaimed(IMftPipelineWorker source, byte[] buffer, long position, int count)
        {
            try
            {
                var active = Interlocked.Increment(ref inRead);
                var start = Stopwatch.GetTimestamp();
                source.Read(position, buffer.AsSpan(0, count));
                var elapsed = Stopwatch.GetTimestamp() - start;
                Interlocked.Decrement(ref inRead);
                Interlocked.Add(ref readTicks, elapsed);
                samples.Enqueue(new ReadSample(elapsed, active));
            }
            finally
            {
                gate.Exit();
            }
        }

        void GatedWorker(int id)
        {
            IMftPipelineWorker? source = null;
            byte[]? buffer = null;
            try
            {
                while (true)
                {
                    activation.WaitActive(id);
                    token.ThrowIfCancellationRequested();
                    source ??= open();
                    buffer ??= new byte[chunkBytes];
                    var gateStart = Stopwatch.GetTimestamp();
                    if (!Claim(out var ordinal, out var position, out var count))
                    {
                        activation.Close();
                        return;
                    }
                    Interlocked.Add(ref gateWaitTicks, Stopwatch.GetTimestamp() - gateStart);
                    ReadClaimed(source, buffer, position, count);
                    var parseStart = Stopwatch.GetTimestamp();
                    var entries = source.Parse(buffer, position, count);
                    Interlocked.Add(ref parseTicks, Stopwatch.GetTimestamp() - parseStart);
                    results.Add(new ParsedChunk(ordinal, entries), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { Fail(error); }
            finally { source?.Dispose(); }
        }

        void Reader()
        {
            IMftPipelineWorker? source = null;
            try
            {
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    source ??= open();
                    var gateStart = Stopwatch.GetTimestamp();
                    if (!Claim(out var ordinal, out var position, out var count))
                        return;
                    Interlocked.Add(ref gateWaitTicks, Stopwatch.GetTimestamp() - gateStart);
                    if (!pool.TryTake(out var buffer))
                        buffer = new byte[chunkBytes];
                    ReadClaimed(source, buffer, position, count);
                    raw.Add(new RawChunk(ordinal, buffer, position, count), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { Fail(error); }
            finally
            {
                source?.Dispose();
                if (Interlocked.Decrement(ref readersAlive) == 0)
                {
                    raw.CompleteAdding();
                    activation.Close();
                }
            }
        }

        void Parser(int id)
        {
            IMftPipelineWorker? source = null;
            try
            {
                while (true)
                {
                    activation.WaitActive(id);
                    source ??= open();
                    var idleStart = Stopwatch.GetTimestamp();
                    if (!raw.TryTake(out var item, Timeout.Infinite, token))
                        return;
                    Interlocked.Add(ref parserIdleTicks, Stopwatch.GetTimestamp() - idleStart);
                    var parseStart = Stopwatch.GetTimestamp();
                    var entries = source.Parse(item.Buffer, item.Position, item.Count);
                    Interlocked.Add(ref parseTicks, Stopwatch.GetTimestamp() - parseStart);
                    pool.Add(item.Buffer);
                    results.Add(new ParsedChunk(item.Ordinal, entries), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception error) { Fail(error); }
            finally { source?.Dispose(); }
        }

        void Lower()
        {
            if (options.BackgroundIo && OperatingSystem.IsWindows())
                SetThreadPriority(GetCurrentThread(), 0x00010000);
        }

        var threads = new List<Thread>();
        for (var index = 0; index < readerThreads; index++)
            threads.Add(new Thread(() => { Lower(); Reader(); }) { IsBackground = true, Name = "mft-lab-reader-" + index });
        for (var index = 0; index < workerThreads; index++)
        {
            var id = index;
            threads.Add(new Thread(() => { Lower(); if (split) Parser(id); else GatedWorker(id); })
            { IsBackground = true, Name = "mft-lab-worker-" + index });
        }

        var allSamples = new List<ReadSample>((int)Math.Min(chunkCount, 1 << 20));
        var changes = new List<MftPipelineChange>();
        var decileMs = new double[10];
        var process = Process.GetCurrentProcess();
        var cpuStart = process.TotalProcessorTime;
        var gcStart = GC.GetTotalPauseDuration();
        var allocatedStart = GC.GetTotalAllocatedBytes();
        var delayTicks = (long)(options.ConsumerMicrosecondsPerChunk * Stopwatch.Frequency / 1_000_000.0);
        var pending = new Dictionary<long, int>();
        long next = 0, entriesTotal = 0, consumerWaitTicks = 0;
        long lastBusyTicks = 0, lastTickStamp = 0;
        var firstChunkMs = 0.0;
        var clock = Stopwatch.StartNew();
        lastTickStamp = Stopwatch.GetTimestamp();
        changes.Add(new MftPipelineChange(0, 0, gate.Limit, activation.Target, 0, 0));
        foreach (var thread in threads)
            thread.Start();
        try
        {
            while (next < chunkCount)
            {
                ParsedChunk chunk;
                var waitStart = Stopwatch.GetTimestamp();
                try { chunk = results.Take(token); }
                catch (OperationCanceledException) when (Volatile.Read(ref failure) is { } error)
                {
                    throw new IOException("Hat işçisi başarısız.", error);
                }
                consumerWaitTicks += Stopwatch.GetTimestamp() - waitStart;
                pending.Add(chunk.Ordinal, chunk.Entries);
                while (pending.Remove(next, out var entries))
                {
                    entriesTotal += entries;
                    next++;
                    if (delayTicks > 0)
                    {
                        var until = Stopwatch.GetTimestamp() + delayTicks;
                        while (Stopwatch.GetTimestamp() < until)
                            Thread.SpinWait(40);
                    }
                    window.Release();
                    if (next == 1)
                        firstChunkMs = clock.Elapsed.TotalMilliseconds;
                    var decile = (int)(next * 10 / chunkCount);
                    if (decile >= 1 && decileMs[decile - 1] == 0)
                        decileMs[decile - 1] = clock.Elapsed.TotalMilliseconds;

                    var changed = false;
                    while (samples.TryDequeue(out var sample))
                    {
                        allSamples.Add(sample);
                        if (gateController is not null &&
                            gateController.Observe(sample.LatencyTicks, sample.Active))
                        {
                            gate.SetLimit(gateController.Limit);
                            changed = true;
                        }
                        if (gateRamp is not null && gateRamp.Observe(sample.LatencyTicks, sample.Active))
                        {
                            gate.SetLimit(gateRamp.Limit);
                            changed = true;
                        }
                    }
                    if (queueController is not null && next % 64 == 0)
                    {
                        var stamp = Stopwatch.GetTimestamp();
                        var windowWait = Interlocked.Read(ref windowWaitTicks);
                        var gateWait = Interlocked.Read(ref gateWaitTicks) - windowWait;
                        if (queueController.Observe((gateWait - lastGateWaitTicks) / (double)Stopwatch.Frequency,
                                (windowWait - lastWindowWaitTicks) / (double)Stopwatch.Frequency,
                                (stamp - lastTickStamp) / (double)Stopwatch.Frequency))
                        {
                            activation.Set(queueController.Workers);
                            changed = true;
                        }
                        lastGateWaitTicks = gateWait;
                        lastWindowWaitTicks = windowWait;
                        lastTickStamp = stamp;
                    }
                    if (workerController is not null && next % 64 == 0)
                    {
                        var stamp = Stopwatch.GetTimestamp();
                        var busy = Interlocked.Read(ref parseTicks) + (split ? 0 : Interlocked.Read(ref readTicks));
                        if (workerController.Observe((busy - lastBusyTicks) / (double)Stopwatch.Frequency,
                                (stamp - lastTickStamp) / (double)Stopwatch.Frequency))
                        {
                            activation.Set(workerController.Workers);
                            changed = true;
                        }
                        lastBusyTicks = busy;
                        lastTickStamp = stamp;
                    }
                    if (changed)
                        changes.Add(new MftPipelineChange(Math.Round(clock.Elapsed.TotalMilliseconds, 1), next,
                            gate.Limit, activation.Target,
                            Math.Round(queueController?.GateQueue ?? gateController?.Queue ?? 0, 2),
                            Math.Round(queueController?.WindowQueue ?? workerController?.Demand ?? 0, 2)));
                }
            }
        }
        finally
        {
            clock.Stop();
            linked.Cancel();
            gate.Close();
            activation.Close();
            foreach (var thread in threads)
                thread.Join();
        }

        while (samples.TryDequeue(out var rest))
            allSamples.Add(rest);
        var wallMs = clock.Elapsed.TotalMilliseconds;
        process.Refresh();
        return new MftPipelineResult
        {
            Mode = options.Mode,
            StartWorkers = startWorkers,
            StartReaders = options.Mode == "combined" ? startWorkers : startReaders,
            FinalWorkers = activation.Target,
            FinalReaders = options.Mode == "combined" ? startWorkers : gate.Limit,
            ChunkBytes = chunkBytes,
            Window = options.Window,
            ConsumerMicrosecondsPerChunk = options.ConsumerMicrosecondsPerChunk,
            Chunks = chunkCount,
            Entries = entriesTotal,
            WallMs = Math.Round(wallMs, 1),
            FirstChunkMs = Math.Round(firstChunkMs, 1),
            MiBPerSecond = Math.Round(totalBytes / (1024.0 * 1024.0) / (wallMs / 1000.0), 1),
            ReadMsSum = Milliseconds(readTicks),
            ParseMsSum = Milliseconds(parseTicks),
            GateWaitMsSum = Milliseconds(Math.Max(0, gateWaitTicks - windowWaitTicks)),
            WindowWaitMsSum = Milliseconds(windowWaitTicks),
            ParserIdleMsSum = Milliseconds(parserIdleTicks),
            ConsumerWaitMs = Milliseconds(consumerWaitTicks),
            GcPauseMs = Math.Round((GC.GetTotalPauseDuration() - gcStart).TotalMilliseconds, 1),
            AllocatedMiB = Math.Round((GC.GetTotalAllocatedBytes() - allocatedStart) / (1024.0 * 1024.0), 1),
            CpuMs = Math.Round((process.TotalProcessorTime - cpuStart).TotalMilliseconds, 1),
            DecileMs = decileMs.Select(value => Math.Round(value, 1)).ToArray(),
            ReadLatency = allSamples.GroupBy(sample => sample.Active).OrderBy(group => group.Key)
                .Select(group => Latency(group.Key, group.Select(sample => sample.LatencyTicks).ToArray())).ToList(),
            Changes = changes
        };
    }

    internal static MftPipelineLatency Latency(int active, long[] ticks)
    {
        Array.Sort(ticks);
        double At(double fraction) => Math.Round(
            ticks[Math.Min(ticks.Length - 1, (int)(ticks.Length * fraction))] * 1000.0 / Stopwatch.Frequency, 3);
        return new MftPipelineLatency(active, ticks.Length, At(0.25), At(0.5), At(0.9));
    }

    private static double Milliseconds(long ticks) => Math.Round(ticks * 1000.0 / Stopwatch.Frequency, 1);

    [System.Runtime.InteropServices.DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentThread();

    [System.Runtime.InteropServices.DllImport("kernel32.dll", SetLastError = true)]
    [return: System.Runtime.InteropServices.MarshalAs(System.Runtime.InteropServices.UnmanagedType.Bool)]
    private static extern bool SetThreadPriority(IntPtr thread, int priority);

    private sealed class ReadGate(int limit)
    {
        private int _limit = Math.Max(1, limit);
        private int _active;
        private bool _closed;

        internal int Limit { get { lock (this) return _limit; } }

        internal bool Enter()
        {
            lock (this)
            {
                while (_active >= _limit && !_closed)
                    Monitor.Wait(this);
                if (_closed)
                    return false;
                _active++;
                return true;
            }
        }

        internal void Exit()
        {
            lock (this)
            {
                _active--;
                Monitor.PulseAll(this);
            }
        }

        internal void SetLimit(int limit)
        {
            lock (this)
            {
                _limit = Math.Max(1, limit);
                Monitor.PulseAll(this);
            }
        }

        internal void Close()
        {
            lock (this)
            {
                _closed = true;
                Monitor.PulseAll(this);
            }
        }
    }

    private sealed class Activation(int target)
    {
        private int _target = target;
        private bool _closed;

        internal int Target { get { lock (this) return _target; } }

        internal void WaitActive(int id)
        {
            lock (this)
            {
                while (id >= _target && !_closed)
                    Monitor.Wait(this);
            }
        }

        internal void Set(int target)
        {
            lock (this)
            {
                _target = target;
                Monitor.PulseAll(this);
            }
        }

        internal void Close()
        {
            lock (this)
            {
                _closed = true;
                Monitor.PulseAll(this);
            }
        }
    }
}
