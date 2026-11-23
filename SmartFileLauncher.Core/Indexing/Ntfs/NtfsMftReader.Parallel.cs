using System.Collections.Concurrent;
using System.Diagnostics;
using System.Threading.Channels;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.Core.Indexing.Ntfs;

public sealed partial class NtfsMftReader
{
    private const int ParallelChunkBytes = 1024 * 1024;
    private const int ParallelMaxInFlight = 32;
    private const int ParallelMaxWorkers = 16;
    internal static readonly ConcurrentDictionary<string, int> RememberedScanWorkers =
        new(StringComparer.OrdinalIgnoreCase);

    internal static int AdaptiveStartWorkers(string? key) =>
        key is not null && RememberedScanWorkers.TryGetValue(key, out var workers)
            ? workers : MftScanWorkerTuner.DefaultStartWorkers;

    public IEnumerable<NtfsMftEntry> ReadEntries(
        IndexBuildOptions options, CancellationToken cancellationToken = default) =>
        ReadEntries(options, null, cancellationToken);

    internal IEnumerable<NtfsMftEntry> ReadEntries(
        IndexBuildOptions options, MftScanTrace? trace, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid)
            throw new ArgumentOutOfRangeException(nameof(options), "İndeks oluşturma seçenekleri geçersiz.");
        return options.ScanMode == IndexScanWorkerMode.Serial
            ? ReadEntries(cancellationToken)
            : ReadEntriesParallel(options, trace, cancellationToken);
    }

    private IEnumerable<NtfsMftEntry> ReadEntriesParallel(
        IndexBuildOptions options, MftScanTrace? trace, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var sourceFactory = _parallelSourceFactory ??
            throw new InvalidOperationException("Paralel MFT taraması için bağımsız birim kaynağı fabrikası gerekli.");
        var totalLength = _mft.Length;
        var recordSize = _source.Geometry.BytesPerRecord;
        if (ParallelChunkBytes % recordSize != 0)
            throw new InvalidDataException("MFT kayıt boyutu paralel okuma bloğunu bölmüyor.");
        var chunkCount = (totalLength + ParallelChunkBytes - 1) / ParallelChunkBytes;
        if (chunkCount <= 0)
            throw new InvalidDataException("MFT veri akışı boş.");
        if (Interlocked.CompareExchange(ref _enumerating, 1, 0) != 0)
            throw new InvalidOperationException("MFT okuyucusu aynı anda yalnız bir tarama çalıştırabilir.");

        using var linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var slots = new SemaphoreSlim(ParallelMaxInFlight, ParallelMaxInFlight);
        var token = linked.Token;
        var channel = Channel.CreateUnbounded<ParallelChunk>(new UnboundedChannelOptions
        {
            SingleReader = true,
            SingleWriter = false,
            AllowSynchronousContinuations = false
        });
        var readers = new ConcurrentBag<NtfsMftReader>();
        Exception? workerError = null;
        var tuner = options.ScanMode == IndexScanWorkerMode.Adaptive
            ? new MftScanWorkerTuner(AdaptiveStartWorkers(_scanWorkerKey)) : null;
        var targetWorkers = tuner?.Target ?? options.ScanWorkers;
        long lastClaimed = -1;
        var workers = Enumerable.Range(0, ParallelMaxWorkers).Select(workerId =>
            Task.Factory.StartNew(() =>
            {
                NtfsMftReader? reader = null;
                byte[]? buffer = null;
                try
                {
                    while (true)
                    {
                        token.ThrowIfCancellationRequested();
                        if (Volatile.Read(ref lastClaimed) >= chunkCount - 1)
                            return;
                        if (workerId >= Volatile.Read(ref targetWorkers))
                        {
                            Thread.Sleep(25);
                            continue;
                        }

                        if (reader is null)
                        {
                            var openStart = Stopwatch.GetTimestamp();
                            reader = OpenParallelReader(sourceFactory, readers, totalLength, recordSize);
                            trace?.ReaderOpened(Stopwatch.GetTimestamp() - openStart);
                        }
                        slots.Wait(token);
                        if (workerId >= Volatile.Read(ref targetWorkers))
                        {
                            slots.Release();
                            continue;
                        }
                        var ordinal = Interlocked.Increment(ref lastClaimed);
                        if (ordinal >= chunkCount)
                        {
                            slots.Release();
                            return;
                        }

                        var position = ordinal * ParallelChunkBytes;
                        var count = (int)Math.Min(ParallelChunkBytes, totalLength - position);
                        var firstChunk = trace is not null && buffer is null;
                        buffer ??= new byte[ParallelChunkBytes];
                        var readStart = firstChunk ? Stopwatch.GetTimestamp() : 0;
                        reader.ReadRawChunk(position, buffer.AsSpan(0, count), token);
                        var parseStart = firstChunk ? Stopwatch.GetTimestamp() : 0;
                        var entries = reader.ReadEntriesFromChunk(buffer, position, count, token).ToArray();
                        if (firstChunk)
                            trace!.FirstChunk(ordinal, parseStart - readStart, Stopwatch.GetTimestamp() - parseStart);
                        if (!channel.Writer.TryWrite(new ParallelChunk(ordinal, entries)))
                            throw new InvalidOperationException("MFT okuma kuyruğu kapandı.");
                    }
                }
                catch (OperationCanceledException) when (token.IsCancellationRequested)
                {
                }
                catch (Exception error)
                {
                    Interlocked.CompareExchange(ref workerError, error, null);
                    channel.Writer.TryComplete(error);
                    linked.Cancel();
                }
            }, CancellationToken.None, TaskCreationOptions.LongRunning,
            TaskScheduler.Default)).ToArray();
        var workersDone = Task.WhenAll(workers);
        _ = workersDone.ContinueWith(_ => channel.Writer.TryComplete(),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously,
            TaskScheduler.Default);

        var pending = new Dictionary<long, ParallelChunk>();
        long next = 0;
        var clock = Stopwatch.StartNew();
        try
        {
            while (next < chunkCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                ParallelChunk chunk;
                if (!channel.Reader.TryRead(out chunk))
                {
                    var waitStart = trace is null ? 0 : Stopwatch.GetTimestamp();
                    try
                    {
                        chunk = channel.Reader.ReadAsync(token).AsTask().GetAwaiter().GetResult();
                    }
                    catch (OperationCanceledException) when (Volatile.Read(ref workerError) is { } failure)
                    {
                        throw new IOException("Paralel MFT okuma işçisi başarısız.", failure);
                    }
                    catch (ChannelClosedException) when (Volatile.Read(ref workerError) is { } failure)
                    {
                        throw new IOException("Paralel MFT okuma işçisi başarısız.", failure);
                    }
                    catch (ChannelClosedException error)
                    {
                        throw new InvalidDataException("MFT okuma kuyruğu tüm bloklar gelmeden kapandı.", error);
                    }
                    trace?.Waited(Stopwatch.GetTimestamp() - waitStart);
                }
                if (chunk.Ordinal < next || !pending.TryAdd(chunk.Ordinal, chunk))
                    throw new InvalidDataException("MFT blok sırası tekrarlı veya geriye dönük.");

                while (pending.Remove(next, out var ready))
                {
                    var deliverStart = trace is null ? 0 : Stopwatch.GetTimestamp();
                    foreach (var entry in ready.Entries)
                        yield return entry;
                    trace?.Delivered(Stopwatch.GetTimestamp() - deliverStart, ready.Entries.Length);
                    next++;
                    slots.Release();
                    var elapsed = clock.Elapsed;
                    trace?.Committed(elapsed, Volatile.Read(ref targetWorkers), next, next == chunkCount);
                    if (tuner is not null)
                        Volatile.Write(ref targetWorkers, tuner.Observe(elapsed, next));
                }
            }
            if (tuner is not null && _scanWorkerKey is not null)
                RememberedScanWorkers[_scanWorkerKey] = tuner.Anchor;
        }
        finally
        {
            linked.Cancel();
            channel.Writer.TryComplete();
            try { workersDone.GetAwaiter().GetResult(); }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            finally
            {
                foreach (var reader in readers)
                    reader.Dispose();
                Volatile.Write(ref _enumerating, 0);
            }
        }
    }

    private NtfsMftReader OpenParallelReader(
        Func<INtfsVolumeSource> sourceFactory, ConcurrentBag<NtfsMftReader> readers,
        long expectedLength, int expectedRecordSize)
    {
        var reader = new NtfsMftReader(sourceFactory());
        try
        {
            if (reader._mft.Length != expectedLength ||
                reader._source.Geometry.BytesPerRecord != expectedRecordSize)
                throw new InvalidDataException("Paralel MFT okuyucularının birim geometrisi değişti.");
            readers.Add(reader);
            return reader;
        }
        catch
        {
            reader.Dispose();
            throw;
        }
    }

    private void ReadRawChunk(long position, Span<byte> buffer, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var recordSize = _source.Geometry.BytesPerRecord;
        var count = buffer.Length;
        if (position < 0 || position % recordSize != 0 || count <= 0 ||
            count % recordSize != 0 || position > _mft.Length - count)
            throw new ArgumentOutOfRangeException(nameof(position));
        ReadMapped(_mft.Runs, position, buffer, cancellationToken);
    }

    private readonly record struct ParallelChunk(long Ordinal, NtfsMftEntry[] Entries);
}
