using System.Globalization;
using System.IO;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

public sealed class FileSystemChangeFeedStore : IChangeFeedStore
{
    public const int DefaultMaximumEntryCount = 512;
    public const long DefaultMaximumTotalBytes = 64L * 1024 * 1024;
    public const long DefaultMaximumEntryBytes = ChangeFeedReadBudget.DefaultMaximumBytes;

    private const string EntrySearchPattern = "*.json";
    private const string TemporarySuffix = ".tmp";
    private const string SequenceFormat = "D19";
    private const int ReplaceAttemptCount = 20;
    private const int ReplaceBackoffMilliseconds = 10;

    private static readonly JsonSerializerOptions SerializerOptions = new()
    {
        WriteIndented = true,
        Converters = { new JsonStringEnumConverter() }
    };

    private readonly ChangeFeedStoreLayout _layout;
    private readonly int _maximumEntryCount;
    private readonly long _maximumTotalBytes;
    private readonly long _maximumEntryBytes;

    public FileSystemChangeFeedStore(
        ChangeFeedStoreLayout layout,
        int maximumEntryCount = DefaultMaximumEntryCount,
        long maximumTotalBytes = DefaultMaximumTotalBytes,
        long maximumEntryBytes = DefaultMaximumEntryBytes)
    {
        _layout = layout ?? throw new ArgumentNullException(nameof(layout));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntryCount);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumTotalBytes);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntryBytes);

        _maximumEntryCount = maximumEntryCount;
        _maximumTotalBytes = maximumTotalBytes;
        _maximumEntryBytes = maximumEntryBytes;
        _layout.EnsureCreated();
    }

    public IDisposable EnterOwnerScope(CancellationToken cancellationToken = default) =>
        ChangeFeedOwnerGate.Enter(_layout.OwnerDirectory, cancellationToken);

    public ChangeFeedSubscription? ReadSubscription()
    {
        using var scope = EnterOwnerScope();


        if (ReadSnapshot(_layout.SubscriptionPath) is not { } payload)
        {
            return null;
        }

        SubscriptionDocument? document;
        try
        {
            document = JsonSerializer.Deserialize<SubscriptionDocument>(payload, SerializerOptions);
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
                        new ChangeFeedRootIdentity(root.VolumeId, root.NodeId),
                        new ChangeFeedRootGeneration(root.Generation)))
                    .ToArray());
        }
        catch (ArgumentException failure)
        {
            throw new InvalidDataException("Abonelik kaydı geçersiz.", failure);
        }
    }

    public void WriteSubscription(ChangeFeedSubscription subscription)
    {
        using var scope = EnterOwnerScope();


        ArgumentNullException.ThrowIfNull(subscription);

        var document = new SubscriptionDocument
        {
            OwnerSid = subscription.OwnerSid,
            Roots = subscription.Roots
                .Select(root => new SubscribedRootDocument
                {
                    RootPath = root.RootPath,
                    VolumeId = root.Identity.VolumeId,
                    NodeId = root.Identity.NodeId,
                    Generation = root.Generation.Value
                })
                .ToList()
        };

        WriteAtomic(
            _layout.SubscriptionPath,
            JsonSerializer.SerializeToUtf8Bytes(document, SerializerOptions));
    }

    public void DeleteSubscription()
    {
        using var scope = EnterOwnerScope();


        for (var attempt = 0; ; attempt++)
        {
            try
            {
                File.Delete(_layout.SubscriptionPath);
                return;
            }
            catch (DirectoryNotFoundException)
            {
                return;
            }
            catch (Exception failure)
                when (IsTransientSharingFailure(failure) && attempt < ReplaceAttemptCount)
            {
                Thread.Sleep(ReplaceBackoffMilliseconds);
            }
        }
    }

    public ChangeFeedQueueSlice ReadPending(ChangeFeedReadBudget? budget = null)
    {
        using var scope = EnterOwnerScope();

        return ReadPending(budget ?? ChangeFeedReadBudget.Default, repaired: false);
    }

    private ChangeFeedQueueSlice ReadPending(ChangeFeedReadBudget limits, bool repaired)
    {
        var files = QueueFiles();
        var entries = new List<ChangeFeedQueueEntry>();
        var bytes = 0L;

        foreach (var file in files)
        {
            var size = SizeOf(file);

            if (entries.Count > 0 &&
                (entries.Count >= limits.MaximumEntries ||
                 bytes + size > limits.MaximumBytes))
            {
                return new ChangeFeedQueueSlice(entries, true);
            }

            ChangeFeedQueueEntry entry;
            byte[] payload;
            try
            {
                payload = File.ReadAllBytes(file);
                entry = ParseEntry(payload, file);
            }
            catch (Exception failure) when (IsUnreadable(failure))
            {
                if (repaired)
                {
                    throw new InvalidDataException(
                        "Teslim kuyruğu onarımdan sonra da okunamıyor.",
                        failure);
                }

                Repair(files, failure);

                return ReadPending(limits, repaired: true);
            }

            entries.Add(entry);
            bytes += payload.Length;
        }

        return new ChangeFeedQueueSlice(entries, false);
    }

    public IReadOnlyList<ChangeFeedQueueEntry> Enqueue(
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> roots)
    {
        ArgumentNullException.ThrowIfNull(roots);

        using var scope = EnterOwnerScope();


        var written = new List<ChangeFeedQueueEntry>();

        foreach (var group in Partition(volumeId, journalId, fromUsn, toUsn, roots))
        {
            var files = QueueFiles();
            var sequence = AllocateSequence(files);
            var candidate = new ChangeFeedQueueEntry(
                sequence,
                volumeId,
                journalId,
                fromUsn,
                toUsn,
                group);
            var payload = JsonSerializer.SerializeToUtf8Bytes(ToDocument(candidate), SerializerOptions);

            if (files.Length >= _maximumEntryCount ||
                TotalBytes(files) + payload.Length > _maximumTotalBytes)
            {
                return Overflow(files, sequence, volumeId, journalId, roots);
            }

            WriteEntry(candidate.Sequence, payload);
            written.Add(candidate);
        }

        return written;
    }

    private List<IReadOnlyList<ChangeFeedRootDelivery>> Partition(
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> roots)
    {
        var groups = new List<IReadOnlyList<ChangeFeedRootDelivery>>();
        Split(volumeId, journalId, fromUsn, toUsn, roots, groups);
        return groups;
    }

    private void Split(
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> group,
        List<IReadOnlyList<ChangeFeedRootDelivery>> groups)
    {
        if (MeasureEntry(volumeId, journalId, fromUsn, toUsn, group) <= _maximumEntryBytes)
        {
            groups.Add(group);
            return;
        }

        if (!CanSplit(group))
        {
            groups.Add(TooLarge(group));
            return;
        }

        var (left, right) = Halve(group);
        Split(volumeId, journalId, fromUsn, toUsn, left, groups);
        Split(volumeId, journalId, fromUsn, toUsn, right, groups);
    }

    private static IReadOnlyList<ChangeFeedRootDelivery> TooLarge(
        IReadOnlyList<ChangeFeedRootDelivery> group) =>
        group
            .Select(delivery => new ChangeFeedRootDelivery(
                delivery.RootPath,
                ChangeFeedBatch.Gap(ChangeFeedGapReason.EntryTooLarge),
                delivery.Generation))
            .ToArray();

    private static bool CanSplit(IReadOnlyList<ChangeFeedRootDelivery> group) =>
        group.Count > 1 ||
        (group[0].Batch.Status == ChangeFeedStatus.Ok && group[0].Batch.Events.Count > 1);

    private static (IReadOnlyList<ChangeFeedRootDelivery> Left, IReadOnlyList<ChangeFeedRootDelivery> Right)
        Halve(IReadOnlyList<ChangeFeedRootDelivery> group)
    {
        if (group.Count > 1)
        {
            var pivot = group.Count / 2;
            return (group.Take(pivot).ToArray(), group.Skip(pivot).ToArray());
        }

        var delivery = group[0];
        var events = delivery.Batch.Events;
        var middle = events.Count / 2;

        return (
            new[] { Slice(delivery, events, 0, middle) },
            new[] { Slice(delivery, events, middle, events.Count - middle) });
    }

    private static ChangeFeedRootDelivery Slice(
        ChangeFeedRootDelivery delivery,
        IReadOnlyList<ChangeFeedEvent> events,
        int offset,
        int count) =>
        new(
            delivery.RootPath,
            ChangeFeedBatch.Ok(events.Skip(offset).Take(count).ToArray()),
            delivery.Generation);

    private static long MeasureEntry(
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> group) =>
        JsonSerializer.SerializeToUtf8Bytes(
            ToDocument(new ChangeFeedQueueEntry(
                long.MaxValue,
                volumeId,
                journalId,
                fromUsn,
                toUsn,
                group)),
            SerializerOptions).Length;

    public void Acknowledge(long sequence)
    {
        using var scope = EnterOwnerScope();


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
        using var scope = EnterOwnerScope();


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

        return ReplaceQueue(
            AllocateSequence(files),
            string.Empty,
            0,
            0,
            0,
            subscription.Roots
                .Select(root => new ChangeFeedRootDelivery(
                    root.RootPath,
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.FeedStateInvalid),
                    root.Generation))
                .ToArray());
    }

    private IReadOnlyList<ChangeFeedQueueEntry> Overflow(
        string[] files,
        long sequence,
        string volumeId,
        ulong journalId,
        IReadOnlyList<ChangeFeedRootDelivery> roots)
    {
        return ReplaceQueue(
            sequence,
            volumeId,
            journalId,
            0,
            0,
            OverflowRoots(files, roots));
    }

    private IReadOnlyList<ChangeFeedRootDelivery> OverflowRoots(
        string[] files,
        IReadOnlyList<ChangeFeedRootDelivery> roots)
    {
        var seen = new Dictionary<string, ChangeFeedRootGeneration>(
            StringComparer.OrdinalIgnoreCase);
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
                seen[root.RootPath] = root.Generation;
            }
        }

        foreach (var root in roots)
        {
            seen[root.RootPath] = root.Generation;
        }

        if (ReadSubscription() is not { } subscription)
        {
            return Array.Empty<ChangeFeedRootDelivery>();
        }

        return subscription.Roots
            .Where(root => unreadable || seen.ContainsKey(root.RootPath))
            .Select(root => OverflowGap(root.RootPath, root.Generation))
            .ToArray();
    }

    private static ChangeFeedRootDelivery OverflowGap(
        string rootPath,
        ChangeFeedRootGeneration generation) =>
        new(
            rootPath,
            ChangeFeedBatch.Gap(ChangeFeedGapReason.DeliveryQueueOverflow),
            generation);

    private IReadOnlyList<ChangeFeedQueueEntry> ReplaceQueue(
        long firstSequence,
        string volumeId,
        ulong journalId,
        long fromUsn,
        long toUsn,
        IReadOnlyList<ChangeFeedRootDelivery> deliveries)
    {
        var groups = deliveries.Count == 0
            ? new List<IReadOnlyList<ChangeFeedRootDelivery>>()
            : Partition(volumeId, journalId, fromUsn, toUsn, deliveries);

        if (groups.Count > 0)
        {
            ReserveSequences(firstSequence, groups.Count);
        }

        var sequence = firstSequence;
        var written = new List<ChangeFeedQueueEntry>(groups.Count);
        var kept = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var group in groups)
        {
            var entry = new ChangeFeedQueueEntry(
                sequence,
                volumeId,
                journalId,
                fromUsn,
                toUsn,
                group);

            var payload = JsonSerializer.SerializeToUtf8Bytes(ToDocument(entry), SerializerOptions);
            var path = EntryPath(sequence);
            WriteAtomic(path, payload);

            kept.Add(path);
            written.Add(entry);
            sequence++;
        }

        foreach (var file in Directory.GetFiles(_layout.QueueDirectory))
        {
            if (!kept.Contains(file))
            {
                File.Delete(file);
            }
        }

        return written;
    }

    private void WriteEntry(long sequence, byte[] payload) =>
        WriteAtomic(EntryPath(sequence), payload);

    private string EntryPath(long sequence) =>
        Path.Combine(
            _layout.QueueDirectory,
            sequence.ToString(SequenceFormat, CultureInfo.InvariantCulture) + ".json");

    private static long SizeOf(string file)
    {
        try
        {
            return new FileInfo(file).Length;
        }
        catch (Exception failure)
            when (failure is FileNotFoundException or DirectoryNotFoundException)
        {
            return 0;
        }
    }

    private string[] QueueFiles()
    {
        var files = Directory.GetFiles(_layout.QueueDirectory, EntrySearchPattern);
        Array.Sort(files, StringComparer.Ordinal);
        return files;
    }

    private long TotalBytes(string[] files) =>
        files.Sum(file => new FileInfo(file).Length);

    private void ReserveSequences(long firstSequence, int count)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(count);

        if (count == 1)
        {
            return;
        }

        WriteAtomic(
            _layout.SequencePath,
            System.Text.Encoding.UTF8.GetBytes(
                (firstSequence + count - 1).ToString(CultureInfo.InvariantCulture)));
    }

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

    private ChangeFeedQueueEntry ReadEntry(string file) =>
        ParseEntry(File.ReadAllBytes(file), file);

    private ChangeFeedQueueEntry ParseEntry(byte[] payload, string file)
    {
        var document = JsonSerializer.Deserialize<QueueDocument>(
            payload,
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

        return new ChangeFeedRootDelivery(
            document.RootPath,
            batch,
            new ChangeFeedRootGeneration(document.Generation));
    }

    private static QueueDocument ToDocument(ChangeFeedQueueEntry entry) =>
        new()
        {
            Sequence = entry.Sequence,
            VolumeId = entry.VolumeId,
            JournalId = entry.JournalId,
            FromUsn = entry.FromUsn,
            ToUsn = entry.ToUsn,
            Roots = entry.Roots.Select(ToDeliveryDocument).ToList()
        };

    private static RootDeliveryDocument ToDeliveryDocument(ChangeFeedRootDelivery delivery) =>
        new()
        {
            RootPath = delivery.RootPath,
            Generation = delivery.Generation.Value,
            Status = delivery.Batch.Status,
            GapReason = delivery.Batch.GapReason,
            FaultReason = delivery.Batch.FaultReason,
            Diagnostics = delivery.Batch.Diagnostics,
            Events = delivery.Batch.Events.Select(ToEventDocument).ToList()
        };

    private static EventDocument ToEventDocument(ChangeFeedEvent change) =>
        new()
        {
            Kind = change.Kind,
            FullPath = change.FullPath,
            IsDirectory = change.IsDirectory,
            OldPath = change.OldPath
        };

    private static bool IsUnreadable(Exception failure) =>
        failure is JsonException or InvalidDataException or ArgumentException;

    private static byte[]? ReadSnapshot(string path)
    {
        for (var attempt = 0; ; attempt++)
        {
            try
            {
                using var stream = new FileStream(
                    path,
                    FileMode.Open,
                    FileAccess.Read,
                    FileShare.ReadWrite | FileShare.Delete);

                using var buffer = new MemoryStream();
                stream.CopyTo(buffer);
                return buffer.ToArray();
            }
            catch (Exception failure)
                when (failure is FileNotFoundException or DirectoryNotFoundException)
            {
                return null;
            }
            catch (Exception failure)
                when (IsTransientSharingFailure(failure) && attempt < ReplaceAttemptCount)
            {
                Thread.Sleep(ReplaceBackoffMilliseconds);
            }
        }
    }

    private static void WriteAtomic(string path, byte[] payload)
    {
        var directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        var temporary = path + "." + Environment.ProcessId.ToString(CultureInfo.InvariantCulture) + TemporarySuffix;

        try
        {
            File.WriteAllBytes(temporary, payload);

            for (var attempt = 0; ; attempt++)
            {
                try
                {
                    File.Move(temporary, path, overwrite: true);
                    return;
                }
                catch (Exception failure)
                    when (IsTransientSharingFailure(failure) && attempt < ReplaceAttemptCount)
                {
                    Thread.Sleep(ReplaceBackoffMilliseconds);
                }
            }
        }
        catch
        {
            TryDelete(temporary);
            throw;
        }
    }

    private static bool IsTransientSharingFailure(Exception failure) =>
        failure is IOException or UnauthorizedAccessException;

    private static void TryDelete(string path)
    {
        try
        {
            File.Delete(path);
        }
        catch (Exception failure) when (failure is IOException or UnauthorizedAccessException)
        {
        }
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

        public string Generation { get; set; } = string.Empty;
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

        public string Generation { get; set; } = string.Empty;

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
