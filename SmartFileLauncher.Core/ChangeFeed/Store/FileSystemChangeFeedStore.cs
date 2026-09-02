using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

public sealed class FileSystemChangeFeedStore : IChangeFeedStore
{
    public const int DefaultMaximumEntryCount = 512;
    public const long DefaultMaximumTotalBytes = 64L * 1024 * 1024;

    private const string EntrySearchPattern = "*.json";
    private const string TemporarySuffix = ".tmp";
    private const string SequenceFormat = "D19";

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ChangeFeedStoreLayout _layout;
    private readonly int _maximumEntryCount;
    private readonly long _maximumTotalBytes;

    public FileSystemChangeFeedStore(
        ChangeFeedStoreLayout layout,
        int maximumEntryCount = DefaultMaximumEntryCount,
        long maximumTotalBytes = DefaultMaximumTotalBytes)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntryCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTotalBytes);

        _maximumEntryCount = maximumEntryCount;
        _maximumTotalBytes = maximumTotalBytes;
        _layout.EnsureCreated();
    }

    public ChangeFeedSubscription? ReadSubscription()
    {
        if (!File.Exists(_layout.SubscriptionPath))
        {
            return null;
        }

        SubscriptionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SubscriptionDocument>(
                File.ReadAllBytes(_layout.SubscriptionPath),
                SerializerOptions);
        }
        catch (JsonException failure)
        {
            throw new InvalidDataException("Abonelik kaydı geçerli JSON değil.", failure);
        }

        if (document is null)
        {
            throw new InvalidDataException("Abonelik kaydı boş.");
        }

        try
        {
            return new ChangeFeedSubscription(
                document.OwnerSid,
                document.Roots
                    .Select(root => new ChangeFeedSubscribedRoot(
                        root.RootPath,
                        new ChangeFeedRootIdentity(root.VolumeId, root.NodeId)))
                    .ToArray());
        }
        catch (ArgumentException failure)
        {
            throw new InvalidDataException("Abonelik kaydı geçersiz.", failure);
        }
    }

    public void WriteSubscription(ChangeFeedSubscription subscription)
    {
        ArgumentNullException.ThrowIfNull(subscription);

        var document = new SubscriptionDocument
        {
            OwnerSid = subscription.OwnerSid,
            Roots = subscription.Roots
                .Select(root => new SubscribedRootDocument
                {
                    RootPath = root.RootPath,
                    VolumeId = root.Identity.VolumeId,
                    NodeId = root.Identity.NodeId
                })
                .ToList()
        };

        WriteAtomic(
            _layout.SubscriptionPath,
            JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions));
    }

    public IReadOnlyList<ChangeFeedQueueEntry> ReadPending()
    {
        var files = QueueFiles();
        var entries = new List<ChangeFeedQueueEntry>(files.Length);

        foreach (var file in files)
        {
            ChangeFeedQueueEntry entry;
            try
            {
                entry = ReadEntry(file);
            }
            catch (Exception failure) when (IsUnreadable(failure))
            {
                return Repair(files, failure);
            }

            entries.Add(entry);
        }

        return entries;
    }

    public ChangeFeedQueueEntry Enqueue(
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        var files = QueueFiles();
        var sequence = AllocateSequence(files);
        var candidate = new ChangeFeedQueueEntry(
            sequence,
            volumeId,
            journalId,
            fromUsn,
            toUsn,
            roots);
        var payload = JsonSerializer.SerializeToUtf8Bytes(ToDocument(candidate), SerializerOptions);

        if (files.Length >= _maximumEntryCount ||
            TotalBytes(files) + payload.Length > _maximumTotalBytes)
        {
            return Overflow(files, sequence, volumeId, journalId, roots);
        }

        WriteEntry(candidate.Sequence, payload);
        return candidate;
    }

    public void Acknowledge(long sequence)
    {
        ArgumentOutOfRangeException.ThrowIfNegative(sequence);

        foreach (var file in QueueFiles())
        {
            if (SequenceOf(file) is long value && value <= sequence)
            {
                File.Delete(file);
            }
        }
    }

    public int DiscardUncommitted(string volumeId, ulong journalId, long committedUsn)
    {
        ArgumentNullException.ThrowIfNull(volumeId);
        ArgumentOutOfRangeException.ThrowIfNegative(committedUsn);

        var discarded = 0;
        foreach (var file in QueueFiles())
        {
            ChangeFeedQueueEntry entry;
            try
            {
                entry = ReadEntry(file);
            }
            catch (Exception failure) when (IsUnreadable(failure))
            {
                continue;
            }

            if (string.Equals(entry.VolumeId, volumeId, StringComparison.OrdinalIgnoreCase) &&
                entry.JournalId == journalId &&
                entry.ToUsn > committedUsn)
            {
                File.Delete(file);
                discarded++;
            }
        }

        return discarded;
    }

    private IReadOnlyList<ChangeFeedQueueEntry> Repair(string[] files, Exception failure)
    {
        var subscription = ReadSubscription()
            ?? throw new InvalidDataException(
                "Teslim kuyruğu bozuk ve onarım için abonelik kaydı yok.",
                failure);

        var sequence = AllocateSequence(files);
        var entry = new ChangeFeedQueueEntry(
            sequence,
            string.Empty,
            0,
            0,
            0,
            subscription.Roots
                .Select(root => new ChangeFeedRootDelivery(
                    root.RootPath,
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.FeedStateInvalid)))
                .ToArray());

        ReplaceQueue(entry);
        return new[] { entry };
    }

    private ChangeFeedQueueEntry Overflow(
        string[] files,
        long sequence,
        string volumeId,
        ulong journalId,
        IReadOnlyList<ChangeFeedRootDelivery> roots)
    {
        var affected = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var unreadable = false;

        foreach (var file in files)
        {
            ChangeFeedQueueEntry pending;
            try
            {
                pending = ReadEntry(file);
            }
            catch (Exception failure) when (IsUnreadable(failure))
            {
                unreadable = true;
                continue;
            }

            foreach (var root in pending.Roots)
            {
                if (seen.Add(root.RootPath))
                {
                    affected.Add(root.RootPath);
                }
            }
        }

        foreach (var root in roots)
        {
            if (seen.Add(root.RootPath))
            {
                affected.Add(root.RootPath);
            }
        }

        if (unreadable && ReadSubscription() is { } subscription)
        {
            foreach (var root in subscription.Roots)
            {
                if (seen.Add(root.RootPath))
                {
                    affected.Add(root.RootPath);
                }
            }
        }

        var entry = new ChangeFeedQueueEntry(
            sequence,
            volumeId,
            journalId,
            0,
            0,
            affected
                .Select(path => new ChangeFeedRootDelivery(
                    path,
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.DeliveryQueueOverflow)))
                .ToArray());

        ReplaceQueue(entry);
        return entry;
    }

    private void ReplaceQueue(ChangeFeedQueueEntry entry)
    {
        var path = EntryPath(entry.Sequence);
        WriteAtomic(
            path,
            JsonSerializer.SerializeToUtf8Bytes(ToDocument(entry), SerializerOptions));

        foreach (var file in Directory.GetFiles(_layout.QueueDirectory))
        {
            if (!string.Equals(file, path, StringComparison.OrdinalIgnoreCase))
            {
                File.Delete(file);
            }
        }
    }

    private void WriteEntry(long sequence, byte[] payload) =>
        WriteAtomic(EntryPath(sequence), payload);

    private string EntryPath(long sequence) =>
        Path.Combine(
            _layout.QueueDirectory,
            sequence.ToString(SequenceFormat, CultureInfo.InvariantCulture) + ".json");

    private string[] QueueFiles()
    {
        var files = Directory.GetFiles(_layout.QueueDirectory, EntrySearchPattern);
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    private long TotalBytes(string[] files) =>
        files.Sum(file => new FileInfo(file).Length);

    private long AllocateSequence(string[] files)
    {
        var highest = ReadSequenceCounter();
        foreach (var file in files)
        {
            if (SequenceOf(file) is long value && value > highest)
            {
                highest = value;
            }
        }

        var next = highest + 1;
        WriteAtomic(
            _layout.SequencePath,
            System.Text.Encoding.UTF8.GetBytes(
                next.ToString(CultureInfo.InvariantCulture)));

        return next;
    }

    private long ReadSequenceCounter()
    {
        if (!File.Exists(_layout.SequencePath))
        {
            return 0;
        }

        try
        {
            return long.TryParse(
                File.ReadAllText(_layout.SequencePath),
                NumberStyles.Integer,
                CultureInfo.InvariantCulture,
                out var value) && value > 0
                ? value
                : 0;
        }
        catch (IOException)
        {
            return 0;
        }
    }

    private static long? SequenceOf(string file) =>
        long.TryParse(
            Path.GetFileNameWithoutExtension(file),
            NumberStyles.Integer,
            CultureInfo.InvariantCulture,
            out var value)
            ? value
            : null;

    private ChangeFeedQueueEntry ReadEntry(string file)
    {
        var document = JsonSerializer.Deserialize<QueueDocument>(
            File.ReadAllBytes(file),
            SerializerOptions)
            ?? throw new InvalidDataException($"Kuyruk girdisi boş: {file}");

        return new ChangeFeedQueueEntry(
            document.Sequence,
            document.VolumeId,
            document.JournalId,
            document.FromUsn,
            document.ToUsn,
            document.Roots.Select(ToDelivery).ToArray());
    }

    private static ChangeFeedRootDelivery ToDelivery(RootDeliveryDocument document)
    {
        var batch = document.Status switch
        {
            ChangeFeedStatus.Ok => ChangeFeedBatch.Ok(
                document.Events
                    .Select(item => new ChangeFeedEvent(
                        item.Kind,
                        item.FullPath,
                        item.IsDirectory,
                        item.OldPath))
                    .ToArray()),
            ChangeFeedStatus.Gap => ChangeFeedBatch.Gap(document.GapReason),
            ChangeFeedStatus.Faulted => ChangeFeedBatch.Faulted(
                document.FaultReason,
                document.Diagnostics ?? "(tanı yok)"),
            _ => throw new InvalidDataException(
                $"Bilinmeyen teslim durumu: {document.Status}")
        };

        return new ChangeFeedRootDelivery(document.RootPath, batch);
    }

    private static QueueDocument ToDocument(ChangeFeedQueueEntry entry) =>
        new()
        {
            Sequence = entry.Sequence,
            VolumeId = entry.VolumeId,
            JournalId = entry.JournalId,
            FromUsn = entry.FromUsn,
            ToUsn = entry.ToUsn,
            Roots = entry.Roots
                .Select(root => new RootDeliveryDocument
                {
                    RootPath = root.RootPath,
                    Status = root.Batch.Status,
                    GapReason = root.Batch.GapReason,
                    FaultReason = root.Batch.FaultReason,
                    Diagnostics = root.Batch.Diagnostics,
                    Events = root.Batch.Events
                        .Select(change => new EventDocument
                        {
                            Kind = change.Kind,
                            FullPath = change.FullPath,
                            IsDirectory = change.IsDirectory,
                            OldPath = change.OldPath
                        })
                        .ToList()
                })
                .ToList()
        };

    private static bool IsUnreadable(Exception failure) =>
        failure is JsonException or InvalidDataException or ArgumentException;

    private static void WriteAtomic(string path, byte[] payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + TemporarySuffix;
        File.WriteAllBytes(temporary, payload);
        File.Move(temporary, path, overwrite: true);
    }

    private sealed class SubscriptionDocument
    {
        public string OwnerSid { get; set; } = string.Empty;

        public List<SubscribedRootDocument> Roots { get; set; } = new();
    }

    private sealed class SubscribedRootDocument
    {
        public string RootPath { get; set; } = string.Empty;

        public string VolumeId { get; set; } = string.Empty;

        public string NodeId { get; set; } = string.Empty;
    }

    private sealed class QueueDocument
    {
        public long Sequence { get; set; }

        public string VolumeId { get; set; } = string.Empty;

        public ulong JournalId { get; set; }

        public long FromUsn { get; set; }

        public long ToUsn { get; set; }

        public List<RootDeliveryDocument> Roots { get; set; } = new();
    }

    private sealed class RootDeliveryDocument
    {
        public string RootPath { get; set; } = string.Empty;

        public ChangeFeedStatus Status { get; set; }

        public ChangeFeedGapReason GapReason { get; set; }

        public ChangeFeedFaultReason FaultReason { get; set; }

        public string? Diagnostics { get; set; }

        public List<EventDocument> Events { get; set; } = new();
    }

    private sealed class EventDocument
    {
        public ChangeFeedEventKind Kind { get; set; }

        public string FullPath { get; set; } = string.Empty;

        public bool IsDirectory { get; set; }

        public string? OldPath { get; set; }
    }
}
