using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace OmniSpot.Benchmarking.Ntfs;

internal static class MftProbeCommand
{
    internal static Command CreateCommand()
    {
        var command = new Command("mft-probe",
            "Ham MFT okuyucusunu salt okunur exploratory koşumla sınar; dosya adlarını çıktıya yazmaz.");
        var volume = new Option<string>("--volume")
        {
            Required = true,
            Description = "Yerel NTFS sürücü kökü (örneğin C:\\). Yönetici okuma yetkisi gerekir."
        };
        var limit = new Option<int>("--max-entries")
        {
            DefaultValueFactory = _ => 100000,
            Description = "En çok kaç ad girdisi okunacağı; 0 tüm MFT'yi okur."
        };
        var output = new Option<string?>("--output")
        {
            Description = "Yeni JSON çıktı dosyası; mevcut dosyanın üzerine yazılmaz."
        };
        var root = new Option<string?>("--root")
        {
            Description = "Bu kökün iki geçişli MFT envanterini sınar; dosya adlarını yazmaz."
        };
        var verify = new Option<bool>("--verify-metadata")
        {
            Description = "--root altındaki dosya metadata'sını normal dosya API'siyle karşılaştırır; süreye bu ek okumalar da dahildir."
        };
        var scan = new Option<string>("--scan")
        {
            DefaultValueFactory = _ => "serial",
            Description = "MFT işçi modu: serial, adaptive veya manual. Paralel modlarda 200 ms'lik tarama kaydı çıktıya eklenir."
        };
        var workers = new Option<int>("--workers")
        {
            DefaultValueFactory = _ => 4,
            Description = "--scan manual için işçi sayısı (1–16)."
        };
        command.Options.Add(volume);
        command.Options.Add(limit);
        command.Options.Add(output);
        command.Options.Add(root);
        command.Options.Add(verify);
        command.Options.Add(scan);
        command.Options.Add(workers);
        command.SetAction(result => Run(result.GetValue(volume)!, result.GetValue(limit),
            result.GetValue(output), result.GetValue(root), result.GetValue(verify),
            result.GetValue(scan)!, result.GetValue(workers)));
        return command;
    }

    private static int Run(string volume, int limit, string? outputPath, string? root, bool verify,
        string scan, int workers)
    {
        if (limit < 0)
        {
            Console.Error.WriteLine("--max-entries negatif olamaz.");
            return 2;
        }
        IndexBuildOptions? options = scan switch
        {
            "serial" => new(IndexScanWorkerMode.Serial),
            "adaptive" => new(IndexScanWorkerMode.Adaptive),
            "manual" => new(IndexScanWorkerMode.Manual, workers),
            _ => null
        };
        if (options is not { IsValid: true })
        {
            Console.Error.WriteLine("--scan serial, adaptive veya manual olmalı; --workers 1–16 arasında olmalı.");
            return 2;
        }
        if (root is not null && options.ScanMode != IndexScanWorkerMode.Serial)
        {
            Console.Error.WriteLine("--root yalnız --scan serial ile kullanılabilir.");
            return 2;
        }
        var trace = options.ScanMode == IndexScanWorkerMode.Serial ? null : new MftScanTrace();
        if (verify && root is null)
        {
            Console.Error.WriteLine("--verify-metadata için --root gerekir.");
            return 2;
        }

        using var cancellation = new CancellationTokenSource();
        ConsoleCancelEventHandler cancel = (_, args) =>
        {
            args.Cancel = true;
            cancellation.Cancel();
        };
        Console.CancelKeyPress += cancel;
        var timer = Stopwatch.StartNew();
        long files = 0;
        long directories = 0;
        var completed = false;
        string? error = null;
        long metadataMismatches = 0;
        try
        {
            using var entries = Read(volume, root, options, trace, cancellation.Token).GetEnumerator();
            while (limit == 0 || files + directories < limit)
            {
                if (!entries.MoveNext())
                {
                    completed = true;
                    break;
                }
                if (entries.Current.IsDirectory)
                    directories++;
                else
                {
                    files++;
                    if (verify)
                    {
                        var actual = new FileInfo(entries.Current.Path);
                        if (actual.Length != entries.Current.SizeBytes ||
                            actual.CreationTimeUtc.Ticks != entries.Current.CreatedTimeUtc ||
                            actual.LastWriteTimeUtc.Ticks != entries.Current.LastWriteTimeUtc ||
                            actual.Attributes != entries.Current.Attributes)
                            metadataMismatches++;
                    }
                }
            }
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or
            Win32Exception or NotSupportedException or OperationCanceledException or ArgumentException)
        {
            error = exception.Message;
        }
        finally
        {
            timer.Stop();
            Console.CancelKeyPress -= cancel;
        }

        var result = new
        {
            Mode = "exploratory",
            Volume = volume,
            FileNameEntries = files,
            DirectoryNameEntries = directories,
            Root = root,
            CompletedVolume = root is null && completed,
            CompletedRoot = root is not null && completed,
            LimitReached = error is null && !completed && files + directories == limit,
            ElapsedMilliseconds = timer.Elapsed.TotalMilliseconds,
            MetadataChecked = verify,
            MetadataMismatches = verify ? (long?)metadataMismatches : null,
            Scan = scan,
            ManualWorkers = options.ScanMode == IndexScanWorkerMode.Manual ? (int?)workers : null,
            ScanSummary = trace is null ? null : Summarize(trace.Samples),
            ReaderOpenMilliseconds = trace?.ReaderOpenMs,
            WorkerFirstChunks = trace?.FirstChunks,
            ScanSamples = trace?.Samples,
            Error = error
        };
        var json = JsonSerializer.Serialize(result, new JsonSerializerOptions { WriteIndented = true });
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
        return error is null && metadataMismatches == 0 ? 0 : 1;
    }

    private static object? Summarize(IReadOnlyList<MftScanSample> samples)
    {
        if (samples.Count == 0)
            return null;
        var share = new SortedDictionary<int, double>();
        var changes = new List<object>();
        double previousElapsed = 0;
        var previousWorkers = 0;
        foreach (var sample in samples)
        {
            share[sample.Workers] = share.GetValueOrDefault(sample.Workers) + sample.ElapsedMs - previousElapsed;
            if (sample.Workers != previousWorkers)
                changes.Add(new { AtMs = Math.Round(previousElapsed), sample.Workers });
            previousElapsed = sample.ElapsedMs;
            previousWorkers = sample.Workers;
        }
        return new
        {
            FirstSampleMs = samples[0].ElapsedMs,
            FirstSampleWaitMs = samples[0].WaitMs,
            LastWorkers = samples[^1].Workers,
            Chunks = samples[^1].Chunks,
            WaitMs = samples.Sum(sample => sample.WaitMs),
            LongestWaitMs = samples.Max(sample => sample.LongestWaitMs),
            LongestWaitAfterStartMs = samples.Skip(1).Select(sample => sample.LongestWaitMs).DefaultIfEmpty().Max(),
            DownstreamMs = samples.Sum(sample => sample.DownstreamMs),
            LongestDownstreamMs = samples.Max(sample => sample.LongestDownstreamMs),
            GcPauseMs = samples.Sum(sample => sample.GcPauseMs),
            MillisecondsByWorkers = share,
            WorkerChanges = changes
        };
    }

    private static IEnumerable<IndexInventoryEntry> Read(string volume, string? root,
        IndexBuildOptions options, MftScanTrace? trace, CancellationToken ct)
    {
        if (root is not null)
        {
            root = Path.GetFullPath(root);
            if (!string.Equals(Path.GetPathRoot(root), Path.GetPathRoot(Path.GetFullPath(volume)),
                StringComparison.OrdinalIgnoreCase))
                throw new ArgumentException("Kök ve volume aynı sürücüde olmalıdır.");
            if (!new UsnFileSystemIdentityProbe().TryReadIdentity(root, out var identity))
                throw new IOException("Kök kimliği okunamadı.");
            var admitted = new ChangeFeedSubscribedRoot(root, identity.ToChangeFeedRootIdentity(),
                ChangeFeedRootGeneration.New());
            foreach (var entry in new NtfsRootInventory().Read([admitted], ct)) yield return entry;
            yield break;
        }
        using var reader = new NtfsMftReader(volume);
        foreach (var entry in reader.ReadEntries(options, trace, ct))
            yield return new IndexInventoryEntry(string.Empty, entry.IsDirectory, entry.Attributes,
                entry.SizeBytes, entry.CreatedTimeUtc, entry.LastWriteTimeUtc);
    }
}
