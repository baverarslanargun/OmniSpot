using System.CommandLine;
using System.ComponentModel;
using System.Diagnostics;
using System.Text.Json;
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
        command.Options.Add(volume);
        command.Options.Add(limit);
        command.Options.Add(output);
        command.Options.Add(root);
        command.Options.Add(verify);
        command.SetAction(result => Run(result.GetValue(volume)!, result.GetValue(limit),
            result.GetValue(output), result.GetValue(root), result.GetValue(verify)));
        return command;
    }

    private static int Run(string volume, int limit, string? outputPath, string? root, bool verify)
    {
        if (limit < 0)
        {
            Console.Error.WriteLine("--max-entries negatif olamaz.");
            return 2;
        }
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
            using var entries = Read(volume, root, cancellation.Token).GetEnumerator();
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

    private static IEnumerable<IndexInventoryEntry> Read(string volume, string? root, CancellationToken ct)
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
        foreach (var entry in reader.ReadEntries(ct))
            yield return new IndexInventoryEntry(string.Empty, entry.IsDirectory, entry.Attributes,
                entry.SizeBytes, entry.CreatedTimeUtc, entry.LastWriteTimeUtc);
    }
}
