using System.Runtime.CompilerServices;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class LiveCatalogReaderContractTests
{
    [Fact]
    public void SealedReaderMatchesExistingCatalogForPathsQueriesAndFilters()
    {
        using var workspace = new TemporaryDirectory();
        CheckReaders(workspace.Path);
        CollectReaders();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckReaders(string directory)
    {
        var records = PackedCatalogTests.Records();
        var path = Path.Combine(directory, "catalog");
        using var live = new LiveCatalog(path, records[0].Item.FullPath);
        Assert.Throws<InvalidOperationException>(() => live.CreateReader());
        foreach (var record in records.Reverse()) live.Add(record);
        live.Seal();
        using var reopened = LiveCatalog.Open(path);
        var expected = CompactSearchState.Create(records.Select(record => record.Item), new BasicTokenizer());
        foreach (var owner in new[] { live, reopened })
        {
            var reader = owner.CreateReader();
            Assert.Equal(records.Length, reader.ItemCount);
            Assert.Equal(0, reader.Find(records[0].Item.FullPath));
            Assert.Equal(-1, reader.Find(@"C:\missing"));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetItem(-1));
            Assert.Throws<ArgumentOutOfRangeException>(() => reader.GetItem(reader.ItemCount));
            foreach (var record in records)
            {
                var id = reader.Find(record.Item.FullPath.ToUpperInvariant());
                Assert.Equal(record.Item, reader.GetItem(id));
                Assert.Equal(record.Item, reader.GetTransientItem(id, new()));
                Assert.Equal(record, owner.Snapshot().FindRecord(record.Item.FullPath));
            }
            Assert.Equal(Enumerable.Range(0, records.Length), records.Select(record => reader.Find(record.Item.FullPath)).Order());
            var actual = CompactSearchState.FromCatalog(reader);
            CheckState(expected, actual);
            CheckEngines(expected, actual);
            Assert.Throws<InvalidOperationException>(() => actual.Compact());
            Assert.Throws<InvalidOperationException>(() => actual.WriteNewBase(Path.Combine(directory, "wrong.bin")));
            Assert.False(File.Exists(Path.Combine(directory, "wrong.bin")));
            Assert.Throws<OperationCanceledException>(() => actual.GetPartial("none", new(true)));
        }
    }

    [Fact]
    public void ImmutableRenameMoveDeleteAndMetadataDeltaKeepOldQueriesValid()
    {
        using var workspace = new TemporaryDirectory();
        CheckChanges(workspace.Path);
        CollectReaders();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static void CheckChanges(string directory)
    {
        var records = PackedCatalogTests.Records();
        using var live = new LiveCatalog(Path.Combine(directory, "changes"), records[0].Item.FullPath);
        foreach (var record in records) live.Add(record);
        live.Seal();
        var actual = live.CreateSearchState();
        var oldQuery = (IQueryCatalogSnapshot)((ISearchStateReader)actual).ForQuery();
        var expected = CompactSearchState.Create(records.Select(record => record.Item), new BasicTokenizer());
        var moved = records[2].Item with { Name = "moved-rapor.pdf", FullPath = @"C:\Root\moved-rapor.pdf", ParentPath = records[0].Item.FullPath, OpenCount = 99, SizeBytes = 3_000_000 };
        var changed = records[4].Item with { CreatedTime = new DateTime(2026, 9, 13), LastWriteTime = new DateTime(2026, 9, 14), OpenCount = 17 };
        var removed = new[] { records[2].Item.FullPath, records[3].Item.FullPath };
        var next = actual.WithRecordChanges(removed, [moved, changed], new BasicTokenizer());
        var nextExpected = expected.WithRecordChanges(removed, [moved, changed], new BasicTokenizer());
        live.Dispose();
        Assert.Throws<ObjectDisposedException>(() => live.CreateReader());
        Assert.Throws<ObjectDisposedException>(() => live.Snapshot());
        CheckState(expected, actual);
        CheckState(expected, oldQuery);
        CheckState(nextExpected, next);
        CheckEngines(nextExpected, next);
        Assert.Equal(moved, Assert.Single(next.Get("moved")));
        Assert.Equal(99, Assert.Single(next.Get("moved")).OpenCount);
        var deleted = next.WithoutPathAndDescendants(records[1].Item.FullPath);
        CheckState(nextExpected.WithoutPathAndDescendants(records[1].Item.FullPath), deleted);
        Assert.Single(deleted.Get("moved"));
        CheckState(nextExpected, next);
        var restoredDelta = actual.RestoreCheckpointDelta(next.ExportCheckpointDelta());
        CheckState(nextExpected, restoredDelta);
    }

    [Fact]
    public void PagesCloseOnlyAfterLastReaderLeaseIsCollected()
    {
        using var workspace = new TemporaryDirectory();
        var (owner, reader, oldSnapshot) = LeaseThenDispose(workspace.Path);
        CollectReaders();
        Assert.False(reader.IsAlive);
        Assert.Throws<ObjectDisposedException>(() => oldSnapshot.Get("root"));
        owner.Dispose();
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static (LiveCatalog, WeakReference, LiveCatalog.LiveCatalogSnapshot) LeaseThenDispose(string directory)
    {
        var owner = new LiveCatalog(Path.Combine(directory, "lease"), @"C:\Root");
        owner.Add(PackedCatalogTests.Records()[0]); owner.Seal();
        var snapshot = owner.Snapshot();
        var reader = owner.CreateReader();
        var state = CompactSearchState.FromCatalog(reader);
        var second = owner.CreateSearchState();
        owner.Dispose();
        Assert.Single(state.Get("root")); Assert.Single(second.Get("root"));
        Assert.Single(snapshot.Get("root"));
        return (owner, new WeakReference(reader), snapshot);
    }

    private static void CheckState(IQueryCatalogSnapshot expected, IQueryCatalogSnapshot actual)
    {
        Assert.Equal(expected.ItemCount, actual.ItemCount);
        Assert.Equal(expected.TokenCount, actual.TokenCount);
        Assert.Equal(Order(expected.GetAllItems()), Order(actual.GetAllItems()));
        Assert.Equal(Order(expected.GetRoots()), Order(actual.GetRoots()));
        foreach (var folder in expected.GetAllItems().Where(item => item.IsDirectory))
        {
            Assert.Equal(Order(expected.GetChildren(folder.FullPath)), Order(actual.GetChildren(folder.FullPath)));
            Assert.Equal(Order(expected.GetDescendants(folder)), Order(actual.GetDescendants(folder)));
        }
        foreach (var query in new[] { "txt", "rapor", "RAP", "İş", "📁", "case", "", "\ud800", "rappr", "moved", "pdf" })
        {
            Assert.Equal(Order(expected.Get(query)), Order(actual.Get(query)));
            Assert.Equal(Order(expected.GetPartial(query)), Order(actual.GetPartial(query)));
            for (var distance = 0; distance < 3; distance++) Assert.Equal(Order(expected.GetFuzzy(query, distance)), Order(actual.GetFuzzy(query, distance)));
        }
        foreach (var filter in new QueryCatalogFilter[]
        {
            new(true, false, false, true, null, null, null, null, 0, 1),
            new(false, true, false, false, null, null, null, null, null, null),
            new(true, true, true, false, new DateTime(2026, 1, 1), new DateTime(2027, 1, 1), null, null, null, null)
        }) Assert.Equal(Order(expected.GetItems(filter)), Order(actual.GetItems(filter)));
    }

    private static void CheckEngines(ISearchStateReader expected, ISearchStateReader actual)
    {
        var tokenizer = new BasicTokenizer(); var scoring = new BasicScoringStrategy();
        var oldSearch = new SearchEngine(_ => expected, tokenizer, scoring);
        var newSearch = new SearchEngine(_ => actual, tokenizer, scoring);
        var oldAdvanced = new AdvancedSearchEngine(_ => expected, tokenizer, scoring);
        var newAdvanced = new AdvancedSearchEngine(_ => actual, tokenizer, scoring);
        foreach (var view in new[]
        {
            ResultView.SearchDefault,
            new ResultView(ItemFilter.None, new ItemSort(SortField.Size, Descending: true)),
            new ResultView(ItemFilter.None, new ItemSort(SortField.Modified, Descending: false)),
            new ResultView(ItemFilter.None, new ItemSort(SortField.Name, Descending: false))
        })
        {
            foreach (var text in new[] { "rapor", "rapor rap", "moved", "İş", "rappr" })
                Assert.Equal(Results(oldSearch.Search(text, 7, view)), Results(newSearch.Search(text, 7, view)));
            foreach (var query in new StructuredQuery[]
            {
                new() { Keywords = ["rapor"], HardExtensions = ["txt"] },
                new() { FilterOnlyMode = true, TargetType = new() { File = 1, Folder = 0 }, SizeFilter = new() { MinMb = 0, MaxMb = 1 } },
                new() { FilterOnlyMode = true, TargetType = new() { File = 0, Folder = 1 } },
                new() { Keywords = ["İş"], IncludeFolderContents = true }
            }) Assert.Equal(Results(oldAdvanced.Search(query, 7, view)), Results(newAdvanced.Search(query, 7, view)));
        }
    }

    private static object[] Results(IEnumerable<SearchResult> results) => results.Select(result => (object)(result.Name, result.FullPath, result.IsDirectory, result.SizeBytes, result.LastWriteTime, result.Score)).ToArray();

    private static void CollectReaders() { GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect(); }
    private static SearchItem[] Order(IEnumerable<SearchItem> items) => items.OrderBy(item => item.FullPath, StringComparer.Ordinal).ToArray();
}
