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

    public IEnumerable<NtfsMftEntry> ReadEntries(
        IndexBuildOptions options, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(options);
        if (!options.IsValid)
            throw new ArgumentOutOfRangeException(nameof(options), "İndeks oluşturma seçenekleri geçersiz.");
        return options.ScanMode == IndexScanWorkerMode.Serial
            ? ReadEntries(cancellationToken)
            : ReadEntriesParallel(options, cancellationToken);
    }

    private IEnumerable<NtfsMftEntry> ReadEntriesParallel(
        IndexBuildOptions options, CancellationToken cancellationToken)
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
        var targetWorkers = options.ScanMode == IndexScanWorkerMode.Adaptive
            ? 1 : options.ScanWorkers;
        long lastClaimed = -1;
        var workers = Enumerable.Range(0, ParallelMaxWorkers).Select(workerId =>
            Task.Factory.StartNew(() =>
            {
                NtfsMftReader? reader = null;
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

                        reader ??= OpenParallelReader(sourceFactory, readers, totalLength, recordSize);
                        var position = ordinal * ParallelChunkBytes;
                        var count = (int)Math.Min(ParallelChunkBytes, totalLength - position);
                        var bytes = reader.ReadRawChunk(position, count, token);
                        var entries = reader.ReadEntriesFromChunk(bytes, position, count, token).ToArray();
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
        long windowEntries = 0;
        var clock = Stopwatch.StartNew();
        var lastDecision = TimeSpan.Zero;
        var bestRate = 0.0;
        var bestWorkers = 1;
        var adaptiveSettled = false;
        try
        {
            while (next < chunkCount)
            {
                cancellationToken.ThrowIfCancellationRequested();
                ObjectDisposedException.ThrowIf(_disposed, this);
                ParallelChunk chunk;
                if (!channel.Reader.TryRead(out chunk))
                {
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
                }
                if (chunk.Ordinal < next || !pending.TryAdd(chunk.Ordinal, chunk))
                    throw new InvalidDataException("MFT blok sırası tekrarlı veya geriye dönük.");

                while (pending.Remove(next, out var ready))
                {
                    foreach (var entry in ready.Entries)
                        yield return entry;
                    windowEntries += ready.Entries.Length;
                    next++;
                    slots.Release();

                    if (options.ScanMode != IndexScanWorkerMode.Adaptive)
                        continue;
                    var elapsed = clock.Elapsed;
                    if (elapsed - lastDecision < TimeSpan.FromMilliseconds(1200))
                        continue;
                    var windowRate = windowEntries /
                        Math.Max(0.001, (elapsed - lastDecision).TotalSeconds);
                    windowEntries = 0;
                    lastDecision = elapsed;
                    var target = Volatile.Read(ref targetWorkers);
                    if (windowRate > bestRate * 1.03)
                    {
                        bestRate = windowRate;
                        bestWorkers = target;
                    }
                    if (!adaptiveSettled && target > bestWorkers &&
                        windowRate <= bestRate * 1.03)
                    {
                        Volatile.Write(ref targetWorkers, bestWorkers);
                        adaptiveSettled = true;
                    }
                    else if (!adaptiveSettled && target < ParallelMaxWorkers)
                    {
                        Volatile.Write(ref targetWorkers,
                            Math.Min(ParallelMaxWorkers, target * 2));
                    }
                }
            }
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

    private byte[] ReadRawChunk(long position, int count, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        var recordSize = _source.Geometry.BytesPerRecord;
        if (position < 0 || position % recordSize != 0 || count <= 0 ||
            count % recordSize != 0 || position > _mft.Length - count)
            throw new ArgumentOutOfRangeException(nameof(position));
        var buffer = new byte[count];
        ReadMapped(_mft.Runs, position, buffer, cancellationToken);
        return buffer;
    }

    private readonly record struct ParallelChunk(long Ordinal, NtfsMftEntry[] Entries);
}
