using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using SmartFileLauncher.Core.Indexing.Ntfs;

namespace OmniSpot.Benchmarking.Ntfs;

internal static class MftPipelineLabCommand
{
    private const int MiB = 1024 * 1024;

    internal static Command CreateCommand()
    {
        var command = new Command("mft-pipeline-lab",
            "MFT okuma/çözme hattı düzenlerini salt okunur exploratory koşumla karşılaştırır; dosya adlarını çıktıya yazmaz.")
        {
            Hidden = true
        };
        var volume = new Option<string>("--volume") { Required = true };
        var mode = new Option<string>("--mode") { DefaultValueFactory = _ => "combined" };
        var workers = new Option<int>("--workers") { DefaultValueFactory = _ => 4 };
        var readers = new Option<int>("--readers") { DefaultValueFactory = _ => 2 };
        var chunkMiB = new Option<int>("--chunk-mib") { DefaultValueFactory = _ => 1 };
        var window = new Option<int>("--window") { DefaultValueFactory = _ => 32 };
        var maxWorkers = new Option<int>("--max-workers") { DefaultValueFactory = _ => 16 };
        var maxReaders = new Option<int>("--max-readers") { DefaultValueFactory = _ => 8 };
        var consumerMicroseconds = new Option<double>("--consumer-us") { DefaultValueFactory = _ => 0 };
        var sampleMiB = new Option<int>("--sample-mib") { DefaultValueFactory = _ => 384 };
        var parsers = new Option<string>("--parsers") { DefaultValueFactory = _ => "1,2,3,4,6,8" };
        var output = new Option<string?>("--output");
        var backgroundIo = new Option<bool>("--background-io");
        var limitMiB = new Option<int>("--limit-mib") { DefaultValueFactory = _ => 0 };
        foreach (var option in new Option[] { volume, mode, workers, readers, chunkMiB, window, maxWorkers,
                     maxReaders, consumerMicroseconds, sampleMiB, parsers, output, backgroundIo, limitMiB })
            command.Options.Add(option);
        command.SetAction(result => Run(result.GetValue(volume)!, result.GetValue(mode)!,
            new MftPipelineLabOptions(result.GetValue(mode)!, result.GetValue(workers), result.GetValue(readers),
                result.GetValue(chunkMiB) * MiB, result.GetValue(window), result.GetValue(maxWorkers),
                result.GetValue(maxReaders), result.GetValue(consumerMicroseconds), result.GetValue(backgroundIo)),
            result.GetValue(sampleMiB), result.GetValue(parsers)!, result.GetValue(output), result.GetValue(limitMiB)));
        return command;
    }

    private static int Run(string volume, string mode, MftPipelineLabOptions options, int sampleMiB,
        string parsers, string? outputPath, int limitMiB)
    {
        using var cancellation = new CancellationTokenSource();
        object? result = null;
        string? error = null;
        var timer = Stopwatch.StartNew();
        try
        {
            using var reader = new NtfsMftReader(volume);
            if (mode == "parse-only")
            {
                result = ParseOnly(reader, sampleMiB,
                    parsers.Split(',', StringSplitOptions.RemoveEmptyEntries).Select(int.Parse).ToArray(),
                    cancellation.Token);
            }
            else
            {
                if (options.ChunkBytes % reader.ProbeRecordSize != 0)
                    throw new ArgumentException("Blok boyutu MFT kayıt boyutunun katı olmalı.");
                var length = limitMiB > 0 ? Math.Min(reader.ProbeMftLength, (long)limitMiB * MiB) : reader.ProbeMftLength;
                result = MftPipelineLab.Run(length,
                    () => new CoreWorker(reader.ProbeOpenWorker(), cancellation.Token), options, cancellation.Token);
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            Win32Exception or NotSupportedException or OperationCanceledException or ArgumentException or
            FormatException or InvalidOperationException)
        {
            error = exception.GetType().Name + ": " + exception.Message +
                (exception.InnerException is { } inner ? " <- " + inner.GetType().Name + ": " + inner.Message : "");
        }
        timer.Stop();

        var json = JsonSerializer.Serialize(new
        {
            Kind = "exploratory",
            Volume = volume,
            Mode = mode,
            ProcessMs = Math.Round(timer.Elapsed.TotalMilliseconds, 1),
            Result = result,
            Error = error
        }, new JsonSerializerOptions
        {
            WriteIndented = true,
            NumberHandling = System.Text.Json.Serialization.JsonNumberHandling.AllowNamedFloatingPointLiterals
        });
        Console.Out.WriteLine(json);
        if (!string.IsNullOrWhiteSpace(outputPath))
        {
            try
            {
                using var file = new FileStream(outputPath, FileMode.CreateNew, FileAccess.Write);
                using var writer = new StreamWriter(file);
                writer.WriteLine(json);
            }
            catch (IOException exception)
            {
                Console.Error.WriteLine($"Ölçüm çıktısı yazılamadı: {exception.Message}");
                return 2;
            }
        }
        return error is null ? 0 : 1;
    }

    private static object ParseOnly(NtfsMftReader reader, int sampleMiB, int[] parserCounts, CancellationToken token)
    {
        var total = reader.ProbeMftLength;
        var totalChunks = total / MiB;
        var perRegion = Math.Max(1, Math.Min(sampleMiB / 3, (int)(totalChunks / 3)));
        var starts = new[] { 0L, Math.Max(0, totalChunks / 2 - perRegion / 2), Math.Max(0, totalChunks - perRegion) };
        var chunks = new List<(long Position, byte[] Buffer, int Region)>();
        var readTimer = Stopwatch.StartNew();
        for (var region = 0; region < starts.Length; region++)
        {
            for (var index = 0; index < perRegion; index++)
            {
                var position = (starts[region] + index) * MiB;
                var buffer = new byte[MiB];
                reader.ProbeReadRaw(position, buffer, token);
                chunks.Add((position, buffer, region));
            }
        }
        readTimer.Stop();

        var pristine = chunks;
        List<(long Position, byte[] Buffer, int Region)> Fresh() =>
            pristine.Select(chunk => (chunk.Position, (byte[])chunk.Buffer.Clone(), chunk.Region)).ToList();

        var regions = new List<object>();
        using (var single = reader.ProbeOpenWorker())
        {
            foreach (var chunk in Fresh().Where((_, index) => index % 4 == 0))
                single.ProbeParse(chunk.Buffer, chunk.Position, MiB, token);
            chunks = Fresh();
            for (var region = 0; region < starts.Length; region++)
            {
                var start = Stopwatch.GetTimestamp();
                long entries = 0;
                foreach (var chunk in chunks.Where(chunk => chunk.Region == region))
                    entries += single.ProbeParse(chunk.Buffer, chunk.Position, MiB, token);
                var seconds = Stopwatch.GetElapsedTime(start).TotalSeconds;
                regions.Add(new
                {
                    Region = region,
                    StartMiB = starts[region],
                    MiB = perRegion,
                    Entries = entries,
                    MiBPerSecond = Math.Round(perRegion / seconds, 1),
                    MsPerMiB = Math.Round(seconds * 1000 / perRegion, 3)
                });
            }
        }

        var runs = new List<object>();
        var process = Process.GetCurrentProcess();
        foreach (var parsers in parserCounts)
        {
            chunks = Fresh();
            GC.Collect();
            GC.WaitForPendingFinalizers();
            long next = -1, entries = 0, parseTicks = 0;
            Exception? failure = null;
            var gcStart = GC.GetTotalPauseDuration();
            var allocatedStart = GC.GetTotalAllocatedBytes();
            process.Refresh();
            var cpuStart = process.TotalProcessorTime;
            using var barrier = new Barrier(parsers + 1);
            var threads = Enumerable.Range(0, parsers).Select(_ => new Thread(() =>
            {
                try
                {
                    using var worker = reader.ProbeOpenWorker();
                    barrier.SignalAndWait(token);
                    while (true)
                    {
                        var index = Interlocked.Increment(ref next);
                        if (index >= chunks.Count)
                            return;
                        var chunk = chunks[(int)index];
                        var start = Stopwatch.GetTimestamp();
                        var count = worker.ProbeParse(chunk.Buffer, chunk.Position, MiB, token);
                        Interlocked.Add(ref parseTicks, Stopwatch.GetTimestamp() - start);
                        Interlocked.Add(ref entries, count);
                    }
                }
                catch (Exception error) { Interlocked.CompareExchange(ref failure, error, null); }
            })
            { IsBackground = true }).ToArray();
            foreach (var thread in threads)
                thread.Start();
            barrier.SignalAndWait(token);
            var wallStart = Stopwatch.GetTimestamp();
            foreach (var thread in threads)
                thread.Join();
            var wall = Stopwatch.GetElapsedTime(wallStart).TotalSeconds;
            if (failure is not null)
                throw new IOException("Çözme işçisi başarısız.", failure);
            process.Refresh();
            runs.Add(new
            {
                Parsers = parsers,
                MiB = chunks.Count,
                Entries = entries,
                WallMs = Math.Round(wall * 1000, 1),
                MiBPerSecond = Math.Round(chunks.Count / wall, 1),
                ParseMsSum = Math.Round(parseTicks * 1000.0 / Stopwatch.Frequency, 1),
                MsPerMiBPerThread = Math.Round(parseTicks * 1000.0 / Stopwatch.Frequency / chunks.Count, 3),
                GcPauseMs = Math.Round((GC.GetTotalPauseDuration() - gcStart).TotalMilliseconds, 1),
                AllocatedMiB = Math.Round((GC.GetTotalAllocatedBytes() - allocatedStart) / (double)MiB, 1),
                CpuMs = Math.Round((process.TotalProcessorTime - cpuStart).TotalMilliseconds, 1)
            });
        }

        return new
        {
            SampleMiB = chunks.Count,
            PreloadMs = Math.Round(readTimer.Elapsed.TotalMilliseconds, 1),
            Regions = regions,
            Runs = runs
        };
    }

    private sealed class CoreWorker(NtfsMftReader reader, CancellationToken token) : IMftPipelineWorker
    {
        public void Read(long position, Span<byte> buffer) => reader.ProbeReadRaw(position, buffer, token);

        public int Parse(byte[] buffer, long position, int count) => reader.ProbeParse(buffer, position, count, token);

        public void Dispose() => reader.Dispose();
    }
}
