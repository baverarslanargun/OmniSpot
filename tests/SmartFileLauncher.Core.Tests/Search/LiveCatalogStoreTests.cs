using System.Runtime.CompilerServices;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class LiveCatalogStoreTests
{
    [Fact]
    public void PointMutationsPreserveOldViewsAndSurviveReopenWithoutRewritingEveryPage()
    {
        using var workspace = new TemporaryDirectory();
        ExerciseChanges(workspace.Path);
        Collect();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExerciseChanges(string directory)
    {
        var records = PackedCatalogTests.Records(); var root = records[0].Item.FullPath;
        var expected = records.ToDictionary(record => record.Item.FullPath, StringComparer.OrdinalIgnoreCase);
        var path = Path.Combine(directory, "catalog");
        var initial = new LiveCatalog(path, root);
        foreach (var record in records) initial.Add(record);
        initial.Seal();
        var initialView = initial.Snapshot();
        var store = new LiveCatalogStore(path, initial);
        var old = store.State;
        var changed = records[2] with { Item = records[2].Item with { Name = "renamed.pdf", FullPath = root + "\\renamed.pdf", ParentPath = root, OpenCount = 37 } };
        Assert.True(store.Commit([new(changed.Item.FullPath, changed, records[2].Item.FullPath)], "batch-1"));
        expected.Remove(records[2].Item.FullPath); expected[changed.Item.FullPath] = changed;
        Check(store, expected.Values); Assert.Equal(records[2].Item, old.Get("rapor").Single(item => item.FullPath == records[2].Item.FullPath));
        var afterRename = store.State;
        Assert.False(store.Commit([new(changed.Item.FullPath, changed, records[2].Item.FullPath)], "batch-1"));
        Assert.Equal(1, store.Sequence);
        store.Commit([new(records[2].Item.FullPath, records[2], changed.Item.FullPath)], "batch-2");
        expected.Remove(changed.Item.FullPath); expected[records[2].Item.FullPath] = records[2];
        Check(store, expected.Values); Assert.Single(afterRename.Get("renamed"));
        store.Commit([new(records[3].Item.FullPath, null)], "batch-3"); expected.Remove(records[3].Item.FullPath);
        Check(store, expected.Values);
        store.Commit([new(records[3].Item.FullPath, records[3])], "batch-4"); expected[records[3].Item.FullPath] = records[3];
        Check(store, expected.Values);
        var movedFolder = records[1] with { Item = records[1].Item with { Name = "Work", FullPath = root + "\\Work" } };
        store.Commit([new(movedFolder.Item.FullPath, movedFolder, records[1].Item.FullPath)], "batch-5");
        foreach (var record in expected.Values.ToArray())
        {
            if (!record.Item.FullPath.StartsWith(records[1].Item.FullPath + "\\", StringComparison.OrdinalIgnoreCase)) continue;
            expected.Remove(record.Item.FullPath);
            var item = record.Item with { FullPath = movedFolder.Item.FullPath + record.Item.FullPath[records[1].Item.FullPath.Length..], ParentPath = movedFolder.Item.FullPath };
            expected[item.FullPath] = record with { Item = item };
        }
        expected.Remove(records[1].Item.FullPath); expected[movedFolder.Item.FullPath] = movedFolder;
        Check(store, expected.Values);
        store.Commit([new(movedFolder.Item.FullPath, null)], "batch-6");
        foreach (var pathToRemove in expected.Keys.Where(key => key == movedFolder.Item.FullPath || key.StartsWith(movedFolder.Item.FullPath + "\\", StringComparison.OrdinalIgnoreCase)).ToArray()) expected.Remove(pathToRemove);
        Check(store, expected.Values);
        var current = store.State;
        store.Dispose();
        Assert.Equal(records[2], initialView.FindRecord(records[2].Item.FullPath));
        Assert.Equal(records.Length, old.ItemCount); Assert.Single(afterRename.Get("renamed"));
        using var reopened = new LiveCatalogStore(path);
        Check(reopened, expected.Values);
        Assert.Equal(Order(current.GetAllItems()), Order(reopened.State.GetAllItems()));
        Assert.Equal("batch-6", reopened.DeliveryId);
    }

    [Theory]
    [InlineData((int)LiveCommitStage.PagesFlushed, false)]
    [InlineData((int)LiveCommitStage.JournalFlushed, true)]
    [InlineData((int)LiveCommitStage.HeadReplaced, true)]
    public void InterruptedCommitRecoversExactlyOneVersion(int stage, bool committed)
    {
        using var workspace = new TemporaryDirectory();
        ExerciseFault(workspace.Path, (LiveCommitStage)stage, committed);
        Collect();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExerciseFault(string directory, LiveCommitStage stage, bool committed)
    {
        var records = PackedCatalogTests.Records(); var path = Path.Combine(directory, "fault");
        var initial = new LiveCatalog(path, records[0].Item.FullPath);
        foreach (var record in records) initial.Add(record); initial.Seal();
        var changed = records[2] with { Item = records[2].Item with { OpenCount = 999, SizeBytes = 12345 } };
        using (var store = new LiveCatalogStore(path, initial))
        {
            store.FaultPoint = at => { if (at == stage) throw new IOException("injected"); };
            Assert.Throws<IOException>(() => store.Commit([new(changed.Item.FullPath, changed)], "source-batch"));
            Assert.Equal(records[2].Item, store.State.Get("rapor").Single(item => item.FullPath == changed.Item.FullPath));
        }
        using var reopened = new LiveCatalogStore(path);
        Assert.Equal(committed ? changed : records[2], reopened.FindRecord(changed.Item.FullPath));
        Assert.Equal(committed ? 1 : 0, reopened.Sequence);
        Assert.False(File.Exists(Path.Combine(path, "transaction.wal")));
        if (committed) Assert.False(reopened.Commit([new(changed.Item.FullPath, changed)], "source-batch"));
    }

    [Fact]
    public void MetadataChangeCopiesOnlyTouchedPagesAndInvalidBatchIsAtomic()
    {
        using var workspace = new TemporaryDirectory();
        ExercisePages(workspace.Path); Collect();
    }
    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void ExercisePages(string directory)
    {
        var root = PackedCatalogTests.Records()[0]; var path = Path.Combine(directory, "pages");
        var initial = new LiveCatalog(path, root.Item.FullPath); initial.Add(root);
        PackedRecord? selected = null;
        for (var index = 0; index < 6000; index++)
        {
            var name = $"long-document-{index}-" + new string('a', 100) + ".txt";
            var record = new PackedRecord(new(name, root.Item.FullPath + "\\" + name, false, index, null, null, 0, root.Item.FullPath), 0, 0, false, false, root.IndexedUtc);
            initial.Add(record); if (index == 123) selected = record;
        }
        initial.Seal(); using var store = new LiveCatalogStore(path, initial);
        var before = LiveCatalogStore.ReadHead(path); var old = store.State;
        var changed = selected! with { Item = selected!.Item with { OpenCount = 1 } };
        store.Commit([new(changed.Item.FullPath, changed)]);
        var after = LiveCatalogStore.ReadHead(path);
        var originalPages = before.Areas.SelectMany(area => area.Pages).Select(page => page.File).ToHashSet();
        var nextPages = after.Areas.SelectMany(area => area.Pages).Select(page => page.File).ToArray();
        Assert.InRange(nextPages.Count(page => !originalPages.Contains(page)), 1, 3);
        Assert.Contains(nextPages, originalPages.Contains);
        Assert.Equal(0, old.Get("123").Single().OpenCount);
        Assert.Equal(1, store.State.Get("123").Single().OpenCount);
        Assert.Throws<InvalidDataException>(() => store.Commit([new(changed.Item.FullPath, selected), new(@"D:\outside.txt", selected)]));
        Assert.Equal(changed, store.FindRecord(changed.Item.FullPath));
        Assert.Equal(1, store.Sequence);
    }

    private static void Check(LiveCatalogStore store, IEnumerable<PackedRecord> records)
    {
        var all = records.ToArray(); var expected = CompactSearchState.Create(all.Select(record => record.Item), new BasicTokenizer());
        var state = store.State;
        Assert.Equal(expected.ItemCount, state.ItemCount); Assert.Equal(expected.TokenCount, state.TokenCount);
        Assert.Equal(all.Count(record => record.Item.IsDirectory), store.DirectoryCount);
        foreach (var record in all) Assert.Equal(record, store.FindRecord(record.Item.FullPath));
        Assert.Equal(Order(expected.GetAllItems()), Order(state.GetAllItems()));
        foreach (var query in new[] { "rapor", "txt", "pdf", "renamed", "Work", "case", "", "0", "1" })
        {
            Assert.Equal(Order(expected.Get(query)), Order(state.Get(query)));
            Assert.Equal(Order(expected.GetPartial(query)), Order(state.GetPartial(query)));
            Assert.Equal(Order(expected.GetFuzzy(query, 1)), Order(state.GetFuzzy(query, 1)));
        }
    }
    private static SearchItem[] Order(IEnumerable<SearchItem> items) => items.OrderBy(item => item.FullPath, StringComparer.Ordinal).ToArray();
    private static void Collect() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
}
