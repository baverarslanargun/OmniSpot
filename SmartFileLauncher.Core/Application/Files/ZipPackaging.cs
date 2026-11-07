using System.IO.Compression;

namespace SmartFileLauncher.Core.Application.Files;

public static class ZipPackaging
{
    private static readonly EnumerationOptions TreeOptions = new()
    {
        RecurseSubdirectories = true,
        IgnoreInaccessible = true,
        AttributesToSkip = FileAttributes.ReparsePoint
    };

    public static string Create(
        IReadOnlyList<string> sources,
        string destinationFolder,
        string archiveName,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(sources);
        ArgumentException.ThrowIfNullOrWhiteSpace(destinationFolder);
        ArgumentException.ThrowIfNullOrWhiteSpace(archiveName);
        if (sources.Count == 0) throw new ArgumentException("Sıkıştırılacak öğe yok.", nameof(sources));

        var archivePath = UniquePath(destinationFolder, archiveName, ".zip");
        try
        {
            using var archive = ZipFile.Open(archivePath, ZipArchiveMode.Create);
            var topLevel = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var source in sources)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var trimmed = source.TrimEnd('\\', '/');
                var entryName = UniqueEntryName(topLevel, Path.GetFileName(trimmed));
                if (Directory.Exists(trimmed))
                {
                    AddDirectory(archive, trimmed, entryName, cancellationToken);
                }
                else if (File.Exists(trimmed))
                {
                    archive.CreateEntryFromFile(trimmed, entryName, CompressionLevel.Optimal);
                }
                else
                {
                    throw new FileNotFoundException($"Kaynak öğe bulunamadı: {trimmed}");
                }
            }
        }
        catch
        {
            TryDelete(archivePath);
            throw;
        }

        return archivePath;
    }

    public static string Extract(string archivePath, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(archivePath);
        var folder = Path.GetDirectoryName(archivePath)
            ?? throw new IOException("Arşivin klasörü bulunamadı.");
        var destination = UniquePath(folder, Path.GetFileNameWithoutExtension(archivePath), string.Empty);
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            ZipFile.ExtractToDirectory(archivePath, destination);
        }
        catch
        {
            if (Directory.Exists(destination)) TryDeleteDirectory(destination);
            throw;
        }

        return destination;
    }

    public static string UniquePath(string folder, string name, string extension)
    {
        var candidate = Path.Combine(folder, name + extension);
        for (var copy = 2; File.Exists(candidate) || Directory.Exists(candidate); copy++)
        {
            candidate = Path.Combine(folder, $"{name} ({copy}){extension}");
        }

        return candidate;
    }

    private static void AddDirectory(
        ZipArchive archive,
        string directory,
        string entryRoot,
        CancellationToken cancellationToken)
    {
        var hasEntries = false;
        foreach (var file in Directory.EnumerateFiles(directory, "*", TreeOptions))
        {
            cancellationToken.ThrowIfCancellationRequested();
            var relative = Path.GetRelativePath(directory, file).Replace('\\', '/');
            archive.CreateEntryFromFile(file, $"{entryRoot}/{relative}", CompressionLevel.Optimal);
            hasEntries = true;
        }

        if (!hasEntries)
        {
            archive.CreateEntry(entryRoot + "/");
        }
    }

    private static string UniqueEntryName(HashSet<string> used, string name)
    {
        var extension = Path.GetExtension(name);
        var stem = name[..(name.Length - extension.Length)];
        var candidate = name;
        for (var copy = 2; !used.Add(candidate); copy++)
        {
            candidate = $"{stem} ({copy}){extension}";
        }

        return candidate;
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    private static void TryDeleteDirectory(string path)
    {
        try { Directory.Delete(path, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }
}
