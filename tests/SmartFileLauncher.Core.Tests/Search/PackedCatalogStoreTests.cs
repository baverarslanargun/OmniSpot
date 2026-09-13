using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class PackedCatalogStoreTests
{
    [Fact]
    public void UpdatesMovesDeletesAndCursorSurviveReopenCheckpointAndRetry()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "catalog.bin");
        var records = PackedCatalogTests.Records();
        PackedCatalogTests.Build(directory.Path, path, records);
        var source = new PackedSourcePosition("C:", 7, 100, Guid.NewGuid());
        var oldFile = records[2];
        var updated = oldFile with { Item = oldFile.Item with { OpenCount = 999, SizeBytes = 987 }, Hidden = false, System = true, IndexedUtc = 638200000000000000 };
        var initial = new[] { new PackedMutation(oldFile.Item.FullPath, updated) };
        using (var store = new PackedCatalogStore(path))
        {
            var reader = store.State;
            store.Commit(0, null, source, initial);
            var length = new FileInfo(path).Length;
            store.Commit(0, null, source, initial);
            Assert.Equal(length, new FileInfo(path).Length);
            Assert.Equal(updated, store.FindRecord(oldFile.Item.FullPath));
            Assert.Equal(oldFile.Item, reader.Get("rapor").Single(item => item.FullPath == oldFile.Item.FullPath));
            Assert.Throws<InvalidDataException>(() => store.Commit(0, null, source, []));
            Assert.Throws<InvalidDataException>(() => store.Commit(1, source, source with { JournalId = 8 }, []));
            Assert.Throws<InvalidOperationException>(() => store.State.WriteNewBase(Path.Combine(directory.Path, "wrong-v3.bin")));
        }
        using (var store = new PackedCatalogStore(path))
        {
            Assert.Equal(updated, store.FindRecord(oldFile.Item.FullPath));
            Assert.Equal(source, store.Position.Source);
            var folder = records[1];
            var affected = records.Where(record => record.Item.FullPath.StartsWith(folder.Item.FullPath, StringComparison.Ordinal)).ToArray();
            var movedRoot = @"C:\Root\Taşınan";
            var mutations = new List<PackedMutation>();
            foreach (var record in affected)
            {
                var current = store.FindRecord(record.Item.FullPath)!;
                var item = current.Item;
                var movedPath = movedRoot + item.FullPath[folder.Item.FullPath.Length..];
                var movedParent = item == folder.Item ? folder.Item.ParentPath : movedRoot + item.ParentPath![folder.Item.FullPath.Length..];
                var moved = current with { Item = item with { Name = item.FullPath == folder.Item.FullPath ? "Taşınan" : item.Name, FullPath = movedPath, ParentPath = movedParent } };
                mutations.Add(new(item.FullPath, null)); mutations.Add(new(movedPath, moved));
            }
            store.Commit(1, source, source with { NextUsn = 200 }, mutations);
            Assert.Null(store.FindRecord(oldFile.Item.FullPath));
            Assert.Equal(999, store.FindRecord(movedRoot + "\\" + oldFile.Item.Name)!.Item.OpenCount);
            var reader = store.State;
            store.Checkpoint();
            Assert.Equal(0, store.State.DeltaCount);
            Assert.Equal(reader.GetAllItems().OrderBy(item => item.FullPath), store.State.GetAllItems().OrderBy(item => item.FullPath));
            Assert.Equal(2, store.Position.Sequence);
        }
        using var reopened = new PackedCatalogStore(path);
        Assert.Equal(2, reopened.Position.Sequence);
        Assert.Equal(200, reopened.Position.Source!.NextUsn);
        Assert.Equal(999, reopened.FindRecord(@"C:\Root\Taşınan\" + oldFile.Item.Name)!.Item.OpenCount);
        var deleted = reopened.State.GetAllItems().Where(item => item.FullPath.StartsWith(@"C:\Root\Taşınan", StringComparison.Ordinal)).Select(item => new PackedMutation(item.FullPath, null)).ToArray();
        reopened.Commit(2, reopened.Position.Source, reopened.Position.Source, deleted);
        Assert.Empty(reopened.State.Get("rapor"));
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(1, true)]
    [InlineData(2, true)]
    [InlineData(3, true)]
    public void InterruptedWritesRecoverWholeTransaction(int pointValue, bool committed)
    {
        var stage = (PackedWriteStage)pointValue;
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "catalog.bin");
        var records = PackedCatalogTests.Records();
        PackedCatalogTests.Build(directory.Path, path, records);
        var source = new PackedSourcePosition("C:", 7, 100, Guid.NewGuid());
        var updated = records[2] with { Item = records[2].Item with { OpenCount = 77 } };
        using (var store = new PackedCatalogStore(path))
        {
            store.FaultPoint = point => { if (point == stage) throw new IOException("injected"); };
            if (stage is PackedWriteStage.JournalHeader or PackedWriteStage.JournalFlushed)
                Assert.Throws<IOException>(() => store.Commit(0, null, source, [new(updated.Item.FullPath, updated)]));
            else
            {
                store.Commit(0, null, source, [new(updated.Item.FullPath, updated)]);
                Assert.Throws<IOException>(() => store.Checkpoint());
            }
        }
        using var reopened = new PackedCatalogStore(path);
        Assert.Equal(committed ? 1 : 0, reopened.Position.Sequence);
        Assert.Equal(committed ? source : null, reopened.Position.Source);
        Assert.Equal(committed ? updated : records[2], reopened.FindRecord(updated.Item.FullPath));
    }

    [Fact]
    public void CorruptCommittedFrameIsRejectedAndDeltaIsBoundedByPackedCheckpoint()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "catalog.bin");
        var records = PackedCatalogTests.Records();
        PackedCatalogTests.Build(directory.Path, path, records);
        string active;
        using (var store = new PackedCatalogStore(path) { CheckpointDeltaLimit = 2 })
        {
            Assert.Throws<IOException>(() => new PackedCatalogStore(path));
            store.Commit(0, null, null, [new(records[2].Item.FullPath, records[2] with { Hidden = !records[2].Hidden })]);
            store.Commit(1, null, null, [new(records[3].Item.FullPath, records[3] with { System = !records[3].System })]);
            Assert.Equal(0, store.State.DeltaCount);
            Assert.Equal(4, BitConverter.ToInt32(File.ReadAllBytes(store.ActivePath), 4));
            store.Commit(2, null, null, []);
            active = store.ActivePath;
        }
        var bytes = File.ReadAllBytes(active); bytes[^1] ^= 1;
        var damaged = Path.Combine(directory.Path, "damaged.bin"); File.WriteAllBytes(damaged, bytes);
        Assert.Throws<InvalidDataException>(() => new PackedCatalogStore(damaged));
    }

    [Theory]
    [InlineData(2)]
    [InlineData(3)]
    public void AutomaticCheckpointFailureDoesNotReportCommittedBatchAsUncommitted(int pointValue)
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "catalog.bin");
        var records = PackedCatalogTests.Records();
        PackedCatalogTests.Build(directory.Path, path, records);
        var mutation = new PackedMutation(records[2].Item.FullPath, records[2] with { Hidden = false });
        using (var store = new PackedCatalogStore(path) { CheckpointDeltaLimit = 1 })
        {
            store.FaultPoint = point => { if ((int)point == pointValue) throw new IOException("maintenance fault"); };
            var result = store.Commit(0, null, null, [mutation]);
            Assert.False(result.Replayed); Assert.NotNull(result.MaintenanceError);
            Assert.Equal(1, store.Position.Sequence);
        }
        using var reopened = new PackedCatalogStore(path);
        Assert.Equal(1, reopened.Position.Sequence);
        Assert.Equal(mutation.Record, reopened.FindRecord(mutation.Path));
        var length = new FileInfo(reopened.ActivePath).Length;
        Assert.True(reopened.Commit(0, null, null, [mutation]).Replayed);
        Assert.Equal(length, new FileInfo(reopened.ActivePath).Length);
    }
}
