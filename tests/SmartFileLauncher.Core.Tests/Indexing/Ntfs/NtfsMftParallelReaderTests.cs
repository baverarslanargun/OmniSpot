using System.Collections.Concurrent;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Indexing.Ntfs;
using Xunit;
using static SmartFileLauncher.Core.Tests.Indexing.Ntfs.NtfsMftTestData;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class NtfsMftParallelReaderTests
{
    [Theory]
    [InlineData(IndexScanWorkerMode.Serial, 1)]
    [InlineData(IndexScanWorkerMode.Manual, 1)]
    [InlineData(IndexScanWorkerMode.Manual, 4)]
    [InlineData(IndexScanWorkerMode.Adaptive, 1)]
    public void WorkerModesKeepTheSerialEntries(IndexScanWorkerMode mode, int workers)
    {
        var volume = new ParallelTestVolume();
        using var reader = new NtfsMftReader(volume.CreateSource(), () => volume.CreateSource(worker: true));
        var expected = reader.ReadEntries().ToArray();
        var actual = reader.ReadEntries(new IndexBuildOptions(ScanMode: mode, ScanWorkers: workers), default).ToArray();
        Assert.Equal(expected, actual);
    }
    [Fact]
    public void ReusedWorkerBufferKeepsShortFinalChunkAndEarlierEntries()
    {
        var volume = new ParallelTestVolume(shortTail: true);
        using var reader = new NtfsMftReader(volume.CreateSource(), () => volume.CreateSource(worker: true));
        var expected = reader.ReadEntries().ToArray();
        var actual = reader.ReadEntries(new IndexBuildOptions(
            ScanMode: IndexScanWorkerMode.Manual, ScanWorkers: 1)).ToArray();

        Assert.Equal(expected, actual);
        Assert.Equal("kisa-son.txt", actual[^1].Name);
        Assert.Equal(5, actual.Length);
        Assert.All(volume.WorkerSources, source => Assert.True(source.Disposed));
    }

    [Fact]
    public void ManualScanUsesIndependentReadersAndYieldsChunksInMftOrder()
    {
        var volume = new ParallelTestVolume { SynchronizeChunkReads = true };
        volume.CorruptRawRecord(1);
        using var reader = new NtfsMftReader(volume.CreateSource(),
            () => volume.CreateSource(worker: true));

        var entries = reader.ReadEntries(new IndexBuildOptions(
            ScanMode: IndexScanWorkerMode.Manual, ScanWorkers: 2)).ToArray();

        Assert.Equal(new[] { "$MFT", "ilk.txt", "ikinci.txt", "son.txt" },
            entries.Select(entry => entry.Name));
        Assert.True(volume.PeakReads >= 2);
        Assert.True(volume.WorkerSources.Count >= 2);
        Assert.All(volume.WorkerSources, source => Assert.True(source.Disposed));
        Assert.Equal(1, volume.RecordReadCount(1));
    }

    [Fact]
    public void AdaptiveScanStartsFromTheWorkerCountRememberedForItsVolume()
    {
        var single = Guid.NewGuid().ToString("N");
        var pair = Guid.NewGuid().ToString("N");
        NtfsMftReader.RememberedScanWorkers[single] = 1;
        NtfsMftReader.RememberedScanWorkers[pair] = 2;
        try
        {
            var serialVolume = new ParallelTestVolume();
            using (var reader = new NtfsMftReader(serialVolume.CreateSource(),
                () => serialVolume.CreateSource(worker: true), single))
                Assert.Equal(4, reader.ReadEntries(new IndexBuildOptions(), default).Count());
            Assert.Single(serialVolume.WorkerSources);

            var parallelVolume = new ParallelTestVolume { SynchronizeChunkReads = true };
            using (var reader = new NtfsMftReader(parallelVolume.CreateSource(),
                () => parallelVolume.CreateSource(worker: true), pair))
                Assert.Equal(4, reader.ReadEntries(new IndexBuildOptions(), default).Count());
            Assert.True(parallelVolume.PeakReads >= 2);
        }
        finally
        {
            NtfsMftReader.RememberedScanWorkers.TryRemove(single, out _);
            NtfsMftReader.RememberedScanWorkers.TryRemove(pair, out _);
        }
    }

    [Fact]
    public void OnlyACompletedAdaptiveScanRemembersItsWorkerCount()
    {
        var key = Guid.NewGuid().ToString("N");
        try
        {
            Assert.Equal(MftScanWorkerTuner.DefaultStartWorkers, NtfsMftReader.AdaptiveStartWorkers(key));
            var volume = new ParallelTestVolume();
            using (var reader = new NtfsMftReader(volume.CreateSource(),
                () => volume.CreateSource(worker: true), key))
            {
                using (var abandoned = reader.ReadEntries(new IndexBuildOptions(), default).GetEnumerator())
                    Assert.True(abandoned.MoveNext());
                Assert.False(NtfsMftReader.RememberedScanWorkers.ContainsKey(key));

                Assert.Equal(4, reader.ReadEntries(new IndexBuildOptions(
                    ScanMode: IndexScanWorkerMode.Manual, ScanWorkers: 2), default).Count());
                Assert.False(NtfsMftReader.RememberedScanWorkers.ContainsKey(key));

                Assert.Equal(4, reader.ReadEntries(new IndexBuildOptions(), default).Count());
            }
            Assert.Equal(MftScanWorkerTuner.DefaultStartWorkers, NtfsMftReader.RememberedScanWorkers[key]);
        }
        finally
        {
            NtfsMftReader.RememberedScanWorkers.TryRemove(key, out _);
        }
    }

    [Fact]
    public async Task WorkerStillOpeningItsReaderDoesNotHoldBackTheOthers()
    {
        var volume = new ParallelTestVolume(shortTail: true);
        using var secondOpening = new ManualResetEventSlim();
        using var releaseSecond = new ManualResetEventSlim();
        var opened = 0;
        using var reader = new NtfsMftReader(volume.CreateSource(), () =>
        {
            if (Interlocked.Increment(ref opened) == 1)
            {
                Assert.True(secondOpening.Wait(TimeSpan.FromSeconds(5)));
                return volume.CreateSource(worker: true);
            }
            secondOpening.Set();
            releaseSecond.Wait();
            return volume.CreateSource(worker: true);
        });
        var trace = new MftScanTrace();
        var enumerator = reader.ReadEntries(new IndexBuildOptions(
            ScanMode: IndexScanWorkerMode.Manual, ScanWorkers: 2), trace, default).GetEnumerator();
        var read = Task.Run(() =>
        {
            var names = new List<string>();
            while (names.Count < 5 && enumerator.MoveNext())
                names.Add(enumerator.Current.Name);
            return names;
        });
        try
        {
            var names = await read.WaitAsync(TimeSpan.FromSeconds(5));

            Assert.Equal(new[] { "$MFT", "ilk.txt", "ikinci.txt", "son.txt", "kisa-son.txt" }, names);
            Assert.Single(trace.ReaderOpenMs);
        }
        finally
        {
            releaseSecond.Set();
            try { await read; } catch (Exception) { }
            enumerator.Dispose();
        }
        Assert.Equal(2, trace.ReaderOpenMs.Count);
    }

    [Fact]
    public void ScanTraceAccountsForEveryChunkAndEntry()
    {
        var volume = new ParallelTestVolume(shortTail: true);
        using var reader = new NtfsMftReader(volume.CreateSource(), () => volume.CreateSource(worker: true));
        var trace = new MftScanTrace();

        var entries = reader.ReadEntries(new IndexBuildOptions(
            ScanMode: IndexScanWorkerMode.Manual, ScanWorkers: 2), trace, default).ToArray();

        var last = trace.Samples[^1];
        Assert.Equal(3, last.Chunks);
        Assert.Equal(entries.Length, last.Entries);
        Assert.Equal(2, last.Workers);
        Assert.All(trace.Samples, sample =>
        {
            Assert.True(sample.WaitMs >= sample.LongestWaitMs && sample.LongestWaitMs >= 0);
            Assert.True(sample.DownstreamMs >= sample.LongestDownstreamMs && sample.LongestDownstreamMs >= 0);
        });
        Assert.Equal(trace.Samples.OrderBy(sample => sample.ElapsedMs), trace.Samples);
    }

    [Fact]
    public void WorkerFailureCancelsAndJoinsAllReadersBeforeItEscapes()
    {
        var volume = new ParallelTestVolume { FailSecondChunk = true };
        using var reader = new NtfsMftReader(volume.CreateSource(),
            () => volume.CreateSource(worker: true));

        var error = Assert.Throws<IOException>(() => reader.ReadEntries(new IndexBuildOptions(
            ScanMode: IndexScanWorkerMode.Manual, ScanWorkers: 2)).ToArray());

        Assert.IsType<IOException>(error.InnerException);
        Assert.NotEmpty(volume.WorkerSources);
        Assert.All(volume.WorkerSources, source => Assert.True(source.Disposed));
        Assert.False(volume.DisposedWhileReading);
    }

    [Fact]
    public async Task EarlyEnumeratorDisposalJoinsWorkersBeforeDisposingTheirSources()
    {
        var volume = new ParallelTestVolume { HoldSecondChunk = true };
        using var reader = new NtfsMftReader(volume.CreateSource(),
            () => volume.CreateSource(worker: true));
        var entries = reader.ReadEntries(new IndexBuildOptions(
            ScanMode: IndexScanWorkerMode.Manual, ScanWorkers: 2));
        var enumerator = entries.GetEnumerator();
        Assert.True(enumerator.MoveNext());
        Assert.True(volume.SecondChunkStarted.Wait(TimeSpan.FromSeconds(5)));

        var disposal = Task.Run(enumerator.Dispose);
        await Task.Delay(50);
        Assert.False(disposal.IsCompleted);
        volume.ReleaseSecondChunk.Set();
        await disposal.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.NotEmpty(volume.WorkerSources);
        Assert.All(volume.WorkerSources, source => Assert.True(source.Disposed));
        Assert.False(volume.DisposedWhileReading);
    }

    [Fact]
    public void InvalidOptionsAreRejectedBeforeEnumeration()
    {
        var volume = new ParallelTestVolume();
        using var reader = new NtfsMftReader(volume.CreateSource());

        Assert.Throws<ArgumentOutOfRangeException>(() => reader.ReadEntries(
            new IndexBuildOptions(ScanMode: IndexScanWorkerMode.Manual, ScanWorkers: 17)));
    }

    private sealed class ParallelTestVolume
    {
        private const int RecordBytes = 1024;
        private const int ChunkBytes = 1024 * 1024;
        private readonly int _mftBytes;
        private const int ClusterBytes = 512;
        private const int MftLcn = 8;
        private const int MftOffset = MftLcn * ClusterBytes;
        private readonly byte[] _volume;
        private readonly Dictionary<ulong, byte[]> _records = [];
        private readonly ConcurrentDictionary<ulong, int> _recordReads = new();
        private int _activeReads;
        private int _peakReads;
        private int _disposedWhileReading;

        internal ParallelTestVolume(bool shortTail = false)
        {
            _mftBytes = 2 * ChunkBytes + (shortTail ? RecordBytes : 0);
            _volume = new byte[MftOffset + _mftBytes];
            Add(0, Record(Basic(), Name("$MFT"),
                Nonresident(0x80, _mftBytes, 0, _mftBytes / ClusterBytes - 1,
                    0x12, (byte)(shortTail ? 0x02 : 0x00), 0x10, MftLcn, 0)));
            Add(1, Record(Basic(), Name("ilk.txt"), Resident(0x80, [1])));
            Add(1024, Record(Basic(), Name("ikinci.txt"), Resident(0x80, [2])));
            Add(2047, Record(Basic(), Name("son.txt"), Resident(0x80, [3])));
            if (shortTail) Add(2048, Record(Basic(), Name("kisa-son.txt"), Resident(0x80, [4, 5])));
        }

        internal bool SynchronizeChunkReads { get; init; }
        internal bool FailSecondChunk { get; init; }
        internal bool HoldSecondChunk { get; init; }
        internal ManualResetEventSlim FirstChunkStarted { get; } = new();
        internal ManualResetEventSlim SecondChunkStarted { get; } = new();
        internal ManualResetEventSlim ReleaseSecondChunk { get; } = new();
        internal ConcurrentBag<ParallelTestSource> WorkerSources { get; } = [];
        internal int PeakReads => Volatile.Read(ref _peakReads);
        internal bool DisposedWhileReading => Volatile.Read(ref _disposedWhileReading) != 0;

        internal INtfsVolumeSource CreateSource(bool worker = false)
        {
            var source = new ParallelTestSource(this, worker);
            if (worker) WorkerSources.Add(source);
            return source;
        }

        internal void CorruptRawRecord(int segment) =>
            _volume[MftOffset + segment * RecordBytes + RecordBytes - 2] ^= 1;

        internal int RecordReadCount(ulong reference) =>
            _recordReads.TryGetValue(reference, out var count) ? count : 0;

        private void Add(int segment, byte[] record)
        {
            _records[(ulong)segment] = record;
            Protect(record).CopyTo(_volume, MftOffset + segment * RecordBytes);
        }

        private byte[]? ReadFileRecord(ulong reference)
        {
            _recordReads.AddOrUpdate(reference, 1, static (_, count) => count + 1);
            return _records.TryGetValue(reference & 0x0000FFFFFFFFFFFF, out var record)
                ? record.ToArray() : null;
        }

        private void ReadAt(long offset, Span<byte> destination)
        {
            var active = Interlocked.Increment(ref _activeReads);
            UpdatePeak(active);
            try
            {
                var logical = offset - MftOffset;
                if (logical < 0 || logical > _volume.Length - MftOffset - destination.Length)
                    throw new ArgumentOutOfRangeException(nameof(offset));
                if (logical >= ChunkBytes)
                {
                    SecondChunkStarted.Set();
                    if (SynchronizeChunkReads)
                        Assert.True(FirstChunkStarted.Wait(TimeSpan.FromSeconds(5)));
                    if (HoldSecondChunk)
                        ReleaseSecondChunk.Wait();
                    if (FailSecondChunk)
                        throw new IOException("İkinci MFT bloğu okunamadı.");
                }
                else if (SynchronizeChunkReads)
                {
                    FirstChunkStarted.Set();
                    Assert.True(SecondChunkStarted.Wait(TimeSpan.FromSeconds(5)));
                }
                _volume.AsSpan(checked((int)offset), destination.Length).CopyTo(destination);
            }
            finally
            {
                Interlocked.Decrement(ref _activeReads);
            }
        }

        private void SourceDisposed()
        {
            if (Volatile.Read(ref _activeReads) != 0)
                Volatile.Write(ref _disposedWhileReading, 1);
        }

        private void UpdatePeak(int active)
        {
            int previous;
            do
            {
                previous = Volatile.Read(ref _peakReads);
                if (previous >= active) return;
            } while (Interlocked.CompareExchange(ref _peakReads, active, previous) != previous);
        }

        internal sealed class ParallelTestSource(ParallelTestVolume owner, bool worker)
            : INtfsVolumeSource
        {
            private int _disposed;

            public NtfsVolumeGeometry Geometry { get; } =
                new(ClusterBytes, ClusterBytes, RecordBytes, 8192, owner._mftBytes);
            internal bool Disposed => Volatile.Read(ref _disposed) != 0;
            internal bool Worker { get; } = worker;

            public byte[]? ReadFileRecord(ulong reference)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                return owner.ReadFileRecord(reference);
            }

            public void ReadAt(long offset, Span<byte> destination)
            {
                ObjectDisposedException.ThrowIf(Disposed, this);
                owner.ReadAt(offset, destination);
            }

            public void Dispose()
            {
                if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
                owner.SourceDisposed();
            }
        }
    }
}
