using SmartFileLauncher.Core.ChangeFeed;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.ChangeFeed;

public sealed class ChangeFeedEntrySplitTests
{
    private static readonly string OwnerSid = TestStoreOwner.Sid;
    private const string Root = @"C:\Kok";
    private const string OtherRoot = @"C:\Diger";
    private const ulong JournalId = 7;
    private const string VolumeId = "ntfs-vsn:0x000000000000ABCD";

    private static readonly ChangeFeedRootGeneration Generation =
        ChangeFeedRootGeneration.New();

    [Fact]
    public void EntryCap_MatchesTheReadBudget()
    {
        Assert.Equal(
            ChangeFeedReadBudget.DefaultMaximumBytes,
            FileSystemChangeFeedStore.DefaultMaximumEntryBytes);
    }

    [Fact]
    public void ASmallDrain_StaysASingleEntry()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, 64 * 1024);

        var written = store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery(Root, 3) });

        Assert.Single(written);
    }

    [Fact]
    public void ALargeDrain_IsSplitAndNoEntryFileExceedsTheCap()
    {
        using var directory = new TemporaryDirectory();
        var cap = 16 * 1024L;
        var store = CreateStore(directory, cap);

        var written = store.Enqueue(VolumeId, JournalId, 0, 10, new[] { Delivery(Root, 400) });

        Assert.True(written.Count > 1, $"Bölme olmadı: {written.Count} girdi.");
        foreach (var file in QueueFiles(directory))
        {
            Assert.True(
                new FileInfo(file).Length <= cap,
                $"{Path.GetFileName(file)} sınırı aşıyor: {new FileInfo(file).Length} > {cap}.");
        }
    }

    [Fact]
    public void Splitting_LosesNoEventAndKeepsOrder()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, 16 * 1024);
        var delivery = Delivery(Root, 400);

        store.Enqueue(VolumeId, JournalId, 0, 10, new[] { delivery });

        var delivered = store
            .ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue))
            .Entries
            .SelectMany(entry => entry.Roots)
            .SelectMany(root => root.Batch.Events)
            .Select(change => change.FullPath)
            .ToArray();

        Assert.Equal(
            delivery.Batch.Events.Select(change => change.FullPath),
            delivered);
    }

    [Fact]
    public void Splitting_KeepsEveryRootOnItsOwnDelivery()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, 16 * 1024);

        store.Enqueue(
            VolumeId,
            JournalId,
            0,
            10,
            new[] { Delivery(Root, 300), Delivery(OtherRoot, 300) });

        var roots = store
            .ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue))
            .Entries
            .SelectMany(entry => entry.Roots)
            .Select(delivery => delivery.RootPath)
            .Distinct()
            .OrderBy(path => path, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(new[] { OtherRoot, Root }, roots);
    }

    [Fact]
    public void AGapDelivery_IsNeverSplit()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, 16 * 1024);

        store.Enqueue(
            VolumeId,
            JournalId,
            0,
            10,
            new[]
            {
                new ChangeFeedRootDelivery(
                    Root,
                    ChangeFeedBatch.Gap(ChangeFeedGapReason.JournalIdChanged),
                    Generation)
            });

        var gaps = store
            .ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue))
            .Entries
            .SelectMany(entry => entry.Roots)
            .Count(delivery => delivery.Batch.HasGap);

        Assert.Equal(1, gaps);
    }

    [Fact]
    public void ASingleOversizedEvent_BecomesALocationlessGapInsteadOfAnUnreadableEntry()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, 1024);

        var secret = new string('u', 4000);
        var huge = new ChangeFeedEvent(
            ChangeFeedEventKind.Created,
            Path.Combine(Root, secret + ".txt"),
            false);

        store.Enqueue(
            VolumeId,
            JournalId,
            0,
            10,
            new[]
            {
                new ChangeFeedRootDelivery(
                    Root,
                    ChangeFeedBatch.Ok(new[] { huge }),
                    Generation)
            });

        var slice = store.ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue));
        var delivery = slice.Entries.SelectMany(entry => entry.Roots).Single();

        Assert.Equal(ChangeFeedGapReason.EntryTooLarge, delivery.Batch.GapReason);
        Assert.Empty(delivery.Batch.Events);
        Assert.Equal(Root, delivery.RootPath);
    }

    [Fact]
    public void EveryWrittenEntry_StaysUnderTheProducerCeiling()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, 1024);

        store.Enqueue(
            VolumeId,
            JournalId,
            0,
            10,
            new[]
            {
                new ChangeFeedRootDelivery(
                    Root,
                    ChangeFeedBatch.Ok(new[]
                    {
                        new ChangeFeedEvent(
                            ChangeFeedEventKind.Renamed,
                            Path.Combine(Root, new string('y', 3000) + ".txt"),
                            false,
                            Path.Combine(Root, new string('e', 3000) + ".txt"))
                    }),
                    Generation)
            });

        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        foreach (var file in Directory.GetFiles(layout.QueueDirectory, "*.json"))
        {
            Assert.True(
                new FileInfo(file).Length <= 1024,
                $"Üretici tavanı aşan kalıcı girdi yazıldı: {new FileInfo(file).Length}");
        }
    }

    [Fact]
    public void FaultDiagnostics_AreTruncatedSoTheEntryStaysBounded()
    {
        var batch = ChangeFeedBatch.Faulted(
            ChangeFeedFaultReason.NativeProtocolRejected,
            new string('t', 50_000));

        Assert.Equal(ChangeFeedBatch.MaximumDiagnosticsLength, batch.Diagnostics!.Length);
    }

    private static void Subscribe(
        FileSystemChangeFeedStore store,
        IReadOnlyList<ChangeFeedRootDelivery> roots) =>
        store.WriteSubscription(new ChangeFeedSubscription(
            OwnerSid,
            roots
                .Select(root => new ChangeFeedSubscribedRoot(
                    root.RootPath,
                    new ChangeFeedRootIdentity("vol-1", root.RootPath),
                    root.Generation))
                .ToArray()));

    private static ChangeFeedRootDelivery Delivery(string rootPath, int events) =>
        new(
            rootPath,
            ChangeFeedBatch.Ok(
                Enumerable
                    .Range(0, events)
                    .Select(index => new ChangeFeedEvent(
                        ChangeFeedEventKind.Created,
                        Path.Combine(rootPath, $"belge-{index:D5}.txt"),
                        false))
                    .ToArray()),
            Generation);

    private static string[] QueueFiles(TemporaryDirectory directory) =>
        Directory.GetFiles(
            ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid).QueueDirectory,
            "*.json");

    [Fact]
    public void FaultDiagnostics_AreNotCutInsideASurrogatePair()
    {
        var cut = ChangeFeedBatch.MaximumDiagnosticsLength;
        var diagnostics = new string('t', cut - 1) + "😀" + new string('t', 100);

        Assert.True(char.IsHighSurrogate(diagnostics[cut - 1]), "Kurulum sınırda bir vekil çift kurmalıydı.");

        var batch = ChangeFeedBatch.Faulted(
            ChangeFeedFaultReason.NativeProtocolRejected,
            diagnostics);

        var shortened = batch.Diagnostics!;

        Assert.Equal(cut - 1, shortened.Length);
        Assert.False(char.IsSurrogate(shortened[^1]), "Kırpılmış tanı eşi olmayan vekil ile bitmemeli.");
        Assert.Equal(shortened, new string(shortened.EnumerateRunes().SelectMany(rune => rune.ToString()).ToArray()));
    }

    [Fact]
    public void AMultiPartReplacement_NeverReusesASequence()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(
            layout,
            maximumEntryCount: 2,
            maximumTotalBytes: FileSystemChangeFeedStore.DefaultMaximumTotalBytes,
            maximumEntryBytes: 2048);

        var many = Enumerable
            .Range(0, 80)
            .Select(index => new ChangeFeedRootDelivery(
                @"C:\Kok\" + new string('k', 60) + index,
                ChangeFeedBatch.Ok(new[]
                {
                    new ChangeFeedEvent(
                        ChangeFeedEventKind.Created,
                        @"C:\Kok\" + new string('k', 60) + index + @".txt",
                        false)
                }),
                Generation))
            .ToArray();

        Subscribe(store, many);
        store.Enqueue(VolumeId, JournalId, 0, 10, many);
        store.Enqueue(VolumeId, JournalId, 10, 20, many);
        store.Enqueue(VolumeId, JournalId, 20, 30, many);

        var replacement = store
            .ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue))
            .Entries
            .Select(entry => entry.Sequence)
            .ToArray();

        Assert.True(replacement.Length > 1, "Kurulum çok parçalı bir kurtarma üretmeliydi.");
        var highest = replacement.Max();

        store.Acknowledge(highest);
        Assert.Empty(store.ReadPending().Entries);

        var fresh = store.EnqueueOne(VolumeId, JournalId, 30, 40, new[] { Delivery(Root, 1) });

        Assert.True(
            fresh.Sequence > highest,
            $"Sequence yeniden kullanıldı: {fresh.Sequence} <= {highest}");
    }

    [Fact]
    public void AnOverflowEntry_IsSplitSoItStaysUnderTheCeiling()
    {
        using var directory = new TemporaryDirectory();
        var layout = ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid);
        var store = new FileSystemChangeFeedStore(
            layout,
            maximumEntryCount: 2,
            maximumTotalBytes: FileSystemChangeFeedStore.DefaultMaximumTotalBytes,
            maximumEntryBytes: 2048);

        var many = Enumerable
            .Range(0, 80)
            .Select(index => new ChangeFeedRootDelivery(
                @"C:\Kok" + new string('k', 60) + index,
                ChangeFeedBatch.Ok(new[]
                {
                    new ChangeFeedEvent(
                        ChangeFeedEventKind.Created,
                        @"C:\Kok" + new string('k', 60) + index + @".txt",
                        false)
                }),
                Generation))
            .ToArray();

        Subscribe(store, many);
        store.Enqueue(VolumeId, JournalId, 0, 10, many);
        store.Enqueue(VolumeId, JournalId, 10, 20, many);
        store.Enqueue(VolumeId, JournalId, 20, 30, many);

        var files = Directory.GetFiles(layout.QueueDirectory, "*.json");
        Assert.NotEmpty(files);

        foreach (var file in files)
        {
            Assert.True(
                new FileInfo(file).Length <= 2048,
                $"Taşma girdisi tavanı aştı: {new FileInfo(file).Length}");
        }

        var reasons = store
            .ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue))
            .Entries
            .SelectMany(entry => entry.Roots)
            .Select(root => root.Batch.GapReason)
            .Distinct()
            .ToArray();

        Assert.Contains(ChangeFeedGapReason.DeliveryQueueOverflow, reasons);
    }

    [Fact]
    public void TheConvertedEntry_IsASingleRootGapAndKeepsItsUsnRange()
    {
        using var directory = new TemporaryDirectory();
        var store = CreateStore(directory, 1024);

        store.Enqueue(
            VolumeId,
            JournalId,
            40,
            90,
            new[]
            {
                new ChangeFeedRootDelivery(
                    Root,
                    ChangeFeedBatch.Ok(new[]
                    {
                        new ChangeFeedEvent(
                            ChangeFeedEventKind.Created,
                            Path.Combine(Root, new string('u', 4000) + ".txt"),
                            false)
                    }),
                    Generation)
            });

        var entry = store
            .ReadPending(new ChangeFeedReadBudget(int.MaxValue, long.MaxValue))
            .Entries
            .Single();

        var delivery = Assert.Single(entry.Roots);
        Assert.Equal(ChangeFeedGapReason.EntryTooLarge, delivery.Batch.GapReason);
        Assert.Equal(40, entry.FromUsn);
        Assert.Equal(90, entry.ToUsn);
        Assert.True(entry.IsPositional);
    }

    private static FileSystemChangeFeedStore CreateStore(
        TemporaryDirectory directory,
        long maximumEntryBytes) =>
        new(
            ChangeFeedStoreLayout.ForOwner(directory.Path, OwnerSid),
            FileSystemChangeFeedStore.DefaultMaximumEntryCount,
            FileSystemChangeFeedStore.DefaultMaximumTotalBytes,
            maximumEntryBytes);
}
