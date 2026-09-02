using System.IO;
using System.Text.Json;

namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed record UsnVolumeFeedState(
    ulong JournalId,
    long NextUsn,
    IReadOnlyList<UsnChangeFeedState> Roots);

public sealed class UsnChangeFeedStateStore
{
    private const string TemporarySuffix = ".tmp";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = false
    };

    private readonly string _filePath;

    public UsnChangeFeedStateStore(string filePath)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(filePath);
        _filePath = filePath;
    }

    public string FilePath => _filePath;

    public UsnVolumeFeedState? Read()
    {
        if (!File.Exists(_filePath))
        {
            return null;
        }

        VolumeDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<VolumeDocument>(
                File.ReadAllBytes(_filePath),
                SerializerOptions);
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException("Akış durumu geçerli JSON değil.", failure);
        }

        if (document is null)
        {
            throw new InvalidDataException("Akış durumu boş.");
        }

        if (document.Roots is null)
        {
            throw new InvalidDataException("Akış durumu kök listesi taşımıyor.");
        }

        if (document.Roots.Any(root => root is null || root.Directories is null))
        {
            throw new InvalidDataException("Akış durumu eksik kök veya dizin listesi taşıyor.");
        }

        var duplicates = document.Roots
            .GroupBy(root => root.RootPath ?? string.Empty, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);

        if (duplicates is not null)
        {
            throw new InvalidDataException(
                $"Akış durumunda yinelenen kök var: {duplicates.Key}");
        }

        try
        {
            var roots = document.Roots
                .Select(root => new UsnChangeFeedState(
                    root.RootPath,
                    new UsnNodeIdentity(
                        root.VolumeSerialNumber,
                        new UsnFileReference(root.ReferenceLow, root.ReferenceHigh)),
                    document.JournalId,
                    document.NextUsn,
                    root.Directories
                        .Select(entry => new UsnDirectoryEntry(
                            new UsnFileReference(entry.Low, entry.High),
                            entry.Name,
                            new UsnFileReference(entry.ParentLow, entry.ParentHigh)))
                        .ToArray(),
                    root.SynchronizedFromUsn))
                .ToArray();

            return new UsnVolumeFeedState(document.JournalId, document.NextUsn, roots);
        }
        catch (ArgumentException failure)
        {
            throw new InvalidDataException("Akış durumu geçersiz.", failure);
        }
    }

    public void Delete()
    {
        if (File.Exists(_filePath))
        {
            File.Delete(_filePath);
        }
    }

    public void Write(ulong journalId, long nextUsn, IReadOnlyList<UsnChangeFeedState> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);
        ArgumentOutOfRangeException.ThrowIfNegative(nextUsn);

        if (roots.Count == 0)
        {
            throw new ArgumentException("En az bir kök durumu gerekiyor.", nameof(roots));
        }

        var document = new VolumeDocument
        {
            JournalId = journalId,
            NextUsn = nextUsn,
            Roots = roots
                .Select(root => new RootDocument
                {
                    RootPath = root.RootPath,
                    VolumeSerialNumber = root.RootIdentity.VolumeSerialNumber,
                    ReferenceLow = root.RootIdentity.FileReference.Low,
                    ReferenceHigh = root.RootIdentity.FileReference.High,
                    SynchronizedFromUsn = root.SynchronizedFromUsn,
                    Directories = root.Directories
                        .Select(entry => new DirectoryDocument
                        {
                            Low = entry.Reference.Low,
                            High = entry.Reference.High,
                            Name = entry.Name,
                            ParentLow = entry.ParentReference.Low,
                            ParentHigh = entry.ParentReference.High
                        })
                        .ToList()
                })
                .ToList()
        };

        var directory = Path.GetDirectoryName(_filePath);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = _filePath + TemporarySuffix;
        File.WriteAllBytes(
            temporary,
            JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions));
        File.Move(temporary, _filePath, overwrite: true);
    }

    private sealed class VolumeDocument
    {
        public ulong JournalId { get; set; }

        public long NextUsn { get; set; }

        public List<RootDocument> Roots { get; set; } = new();
    }

    private sealed class RootDocument
    {
        public string RootPath { get; set; } = string.Empty;

        public ulong VolumeSerialNumber { get; set; }

        public ulong ReferenceLow { get; set; }

        public ulong ReferenceHigh { get; set; }

        public long SynchronizedFromUsn { get; set; }

        public List<DirectoryDocument> Directories { get; set; } = new();
    }

    private sealed class DirectoryDocument
    {
        public ulong Low { get; set; }

        public ulong High { get; set; }

        public string Name { get; set; } = string.Empty;

        public ulong ParentLow { get; set; }

        public ulong ParentHigh { get; set; }
    }
}
