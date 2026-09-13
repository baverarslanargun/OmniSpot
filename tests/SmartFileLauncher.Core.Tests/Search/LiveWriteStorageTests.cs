using System.Security.Cryptography;
using System.Text.Json;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class LiveWriteStorageTests
{
    [Fact]
    public void PatchRebaseAndLaterPatchPreserveSnapshotsAndReopen()
    {
        using var space = new TemporaryDirectory();
        using var initial = new LivePages(space.Path, "data");
        initial.Allocate(LivePages.PageSize); initial.PutInt32(16, 42); initial.FlushDurable();
        using var old = initial.Fork(false);
        using var small = initial.Fork(true);
        small.PutInt32(16, 43); small.FlushDurable();
        initial.RetireReplacedPages(small);
        var patch = Assert.Single(small.References());
        Assert.NotNull(patch.BaseFile); Assert.True(File.Exists(Path.Combine(space.Path, patch.BaseFile)));
        Assert.InRange(new FileInfo(Path.Combine(space.Path, patch.File)).Length, 1, 100);
        using (var reopened = new LivePages(space.Path, "data", LivePages.PageSize, [patch])) Assert.Equal(43, reopened.Int32(16));
        using var large = small.Fork(true);
        var bytes = Enumerable.Repeat((byte)7, LivePages.PageSize / 2).ToArray();
        large.Put(0, bytes); large.FlushDurable(); small.RetireReplacedPages(large);
        var raw = Assert.Single(large.References()); Assert.Null(raw.BaseFile);
        Assert.Equal(LivePages.PageSize, new FileInfo(Path.Combine(space.Path, raw.File)).Length);
        Assert.Equal(42, old.Int32(16)); Assert.Equal(43, small.Int32(16));
        using var final = large.Fork(true);
        final.PutInt32(16, 44); final.FlushDurable(); large.RetireReplacedPages(final);
        var finalPatch = Assert.Single(final.References()); Assert.Equal(raw.File, finalPatch.BaseFile);
        using var restored = new LivePages(space.Path, "data", LivePages.PageSize, [finalPatch]);
        Assert.Equal(44, restored.Int32(16)); Assert.Equal(7, restored.At(1000));
    }

    [Fact]
    public void RevertingToBaselineAndWritingIdenticalBytesDoesNotCreateFullPages()
    {
        using var space = new TemporaryDirectory();
        using var initial = new LivePages(space.Path, "data"); initial.Allocate(64); initial.FlushDurable();
        using var unchanged = initial.Fork(true); unchanged.PutInt32(0, 0); unchanged.FlushDurable();
        Assert.Equal(initial.References(), unchanged.References());
        using var changed = initial.Fork(true); changed.PutInt32(0, 1); changed.FlushDurable();
        using var reverted = changed.Fork(true); reverted.PutInt32(0, 0); reverted.FlushDurable();
        var patch = Assert.Single(reverted.References());
        Assert.InRange(new FileInfo(Path.Combine(space.Path, patch.File)).Length, 12, 100);
        using var restored = new LivePages(space.Path, "data", 64, [patch]); Assert.Equal(0, restored.Int32(0));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CorruptPatchOrBaseIsRejected(bool corruptBase)
    {
        using var space = new TemporaryDirectory();
        LivePages.PageReference reference;
        using (var initial = new LivePages(space.Path, "data"))
        {
            initial.Allocate(64); initial.FlushDurable();
            using var changed = initial.Fork(true); changed.PutInt32(0, 1); changed.FlushDurable();
            reference = Assert.Single(changed.References());
        }
        var file = Path.Combine(space.Path, corruptBase ? reference.BaseFile! : reference.File);
        var bytes = File.ReadAllBytes(file); bytes[^1] ^= 1; File.WriteAllBytes(file, bytes);
        Assert.Throws<InvalidDataException>(() => new LivePages(space.Path, "data", 64, [reference]));
    }

    [Theory]
    [InlineData((int)LiveCommitStage.PagesFlushed, false)]
    [InlineData((int)LiveCommitStage.JournalFlushed, true)]
    [InlineData((int)LiveCommitStage.HeadReplaced, true)]
    public void CheckpointBoundaryRetainsExactlyTheDurableVersion(int stage, bool committed)
    {
        using var space = new TemporaryDirectory(); var records = PackedCatalogTests.Records(); var path = Path.Combine(space.Path, "catalog");
        var initial = new LiveCatalog(path, records[0].Item.FullPath);
        foreach (var record in records) initial.Add(record); initial.Seal();
        using (var store = new LiveCatalogStore(path, initial))
        {
            for (var count = 1; count <= 64; count++)
                store.Commit([new(records[2].Item.FullPath, records[2] with { Item = records[2].Item with { OpenCount = count } })], "batch-" + count);
            Assert.Equal(64, Directory.GetFiles(path, "commit-*.bin").Length);
            store.FaultPoint = at => { if ((int)at == stage) throw new IOException("injected"); };
            Assert.Throws<IOException>(() => store.Commit([new(records[2].Item.FullPath, records[2] with { Item = records[2].Item with { OpenCount = 65 } })], "batch-65"));
        }
        using var reopened = new LiveCatalogStore(path);
        Assert.Equal(committed ? 65 : 64, reopened.FindRecord(records[2].Item.FullPath)!.Item.OpenCount);
        Assert.Equal(committed ? "batch-65" : "batch-64", reopened.DeliveryId);
        Assert.InRange(Directory.GetFiles(path, "commit-*.bin").Length, 0, 1);
    }

    [Fact]
    public void VersionTwoRawCatalogUpgradesWithoutRebuildAndRejectsCorruptDelta()
    {
        using var space = new TemporaryDirectory(); var records = PackedCatalogTests.Records(); var path = Path.Combine(space.Path, "catalog");
        var initial = new LiveCatalog(path, records[0].Item.FullPath);
        foreach (var record in records) initial.Add(record); initial.Seal();
        using (var store = new LiveCatalogStore(path, initial)) { }
        var frame = LiveCatalogStore.ReadHead(path) with { Version = 2 };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(frame);
        File.WriteAllBytes(Path.Combine(path, "head.bin"), bytes.Concat(SHA256.HashData(bytes)).ToArray());
        using (var store = new LiveCatalogStore(path))
        {
            Assert.Equal(records[2], store.FindRecord(records[2].Item.FullPath));
            store.Commit([new(records[2].Item.FullPath, records[2] with { Item = records[2].Item with { OpenCount = 9 } })]);
        }
        using (var restored = new LiveCatalogStore(path)) Assert.Equal(9, restored.FindRecord(records[2].Item.FullPath)!.Item.OpenCount);
        var commit = Assert.Single(Directory.GetFiles(path, "commit-*.bin"));
        bytes = File.ReadAllBytes(commit); bytes[10] ^= 1; File.WriteAllBytes(commit, bytes);
        Assert.Throws<InvalidDataException>(() => new LiveCatalogStore(path));
    }
}
