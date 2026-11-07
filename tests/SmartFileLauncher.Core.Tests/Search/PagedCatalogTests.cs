using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Search.Paged;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class PagedCatalogTests
{
    [Fact]
    public void BackgroundNamePlanPreservesLaterChangesAndPreviousReaders()
    {
        using var fixture = new Fixture();
        using var baseline = fixture.Build(buildNameOrder: false);
        using var writer = new CatalogStore(baseline, true);
        var plan = baseline.CreateNameOrder(default);
        var reusedId = baseline.Find(10);
        writer.Commit(catalog =>
        {
            catalog.Apply(new(9, 7, "rapor.pdf", Fixture.Entry(9, 7, "çalışma.pdf") with { SizeBytes = 777 }, 3));
            catalog.Apply(new(10, 5, "backup.pdf", null));
            catalog.Apply(new(12, 5, "alpha.pdf", Fixture.Entry(12, 5, "alpha.pdf")));
            catalog.Apply(new(13, 5, "RAPOR.pdf", Fixture.Entry(13, 5, "RAPOR.pdf")));
            catalog.Apply(new(14, 7, "rapor.pdf", Fixture.Entry(14, 7, "rapor.pdf"), 2));
        }, 42, "later-delivery");
        using var before = writer.Acquire();
        Assert.Equal(reusedId, before.Find(12));
        Assert.Equal(0, before.Data.SortedCount);
        writer.Commit(catalog => catalog.ApplyNameOrder(plan, default), 42);
        using var after = writer.Acquire();
        Assert.Equal(plan.Order.Length, after.Data.SortedCount);
        Assert.Equal(42, after.Data.Cursor);
        Assert.Equal("later-delivery", after.Data.DeliveryId);
        Assert.Equal(777, after.Meta(after.Find(9)).Size);
        Assert.Equal(3, after.Meta(after.Find(9)).OpenCount);
        Assert.Equal("çalışma.pdf", after.Name(after.Find(9)));
        Assert.Equal("alpha.pdf", after.Name(reusedId));
        Assert.Equal("rapor.pdf", baseline.Name(baseline.Find(9)));
        Assert.Equal(0, baseline.Data.SortedCount);
        Assert.Equal(0, before.Data.SortedCount);
        foreach (var view in new[] { ResultView.SearchDefault,
            new ResultView(ItemFilter.None, new ItemSort(SortField.Name, false)),
            new ResultView(ItemFilter.None, new ItemSort(SortField.Name, true)) })
        {
            AssertResults(before.Search("pdf", 50, view: view).Results, after.Search("pdf", 50, view: view).Results);
            AssertResults(before.Search("pdf", 2, 1, view).Results, after.Search("pdf", 2, 1, view).Results);
        }
        using var reopened = CatalogStore.Open(fixture.Directory);
        using var disk = reopened.Acquire();
        AssertResults(after.Search("pdf").Results, disk.Search("pdf").Results);
        baseline.Data.ValidateAll();
        before.Data.ValidateAll();
        after.Data.ValidateAll();
    }

    [Fact]
    public async Task UnsortedCatalogWorksAfterCancellationAndPublishesPreparationOnce()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build(buildNameOrder: false);
        using var writer = new CatalogStore(catalog, true);
        using var reader = CatalogStore.Open(fixture.Directory);
        using var previous = reader.Acquire();
        var view = new ResultView(ItemFilter.None, new ItemSort(SortField.Name, true));
        var expected = previous.Search("pdf", view: view).Results;
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.PrepareNameOrderAsync(cancellation.Token));
        using (var unchanged = writer.Acquire()) Assert.Equal(previous.Data.Frame, unchanged.Data.Frame);
        Assert.True(await writer.PrepareNameOrderAsync(default));
        Assert.False(await writer.PrepareNameOrderAsync(default));
        reader.Refresh();
        using var prepared = reader.Acquire();
        Assert.True(prepared.Data.SortedCount > 0);
        AssertResults(expected, prepared.Search("pdf", view: view).Results);
        Assert.Equal(0, previous.Data.SortedCount);
        writer.Compact();
        reader.Refresh();
        using var compacted = reader.Acquire();
        AssertResults(expected, compacted.Search("pdf", view: view).Results);
    }

    [Fact]
    public void ReaderDeltaSharesUnchangedPagesAndKeepsPreviousSnapshotAlive()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        using var writer = new CatalogStore(catalog, true);
        using var reader = CatalogStore.Open(fixture.Directory);
        using var prior = reader.Acquire();
        writer.Apply([new(9, 7, "rapor.pdf", Fixture.Entry(9, 7, "rapor.pdf") with { SizeBytes = 777 })], 1);
        reader.Refresh();
        using var next = reader.Acquire();
        Assert.Same(prior.Data.Areas[Image.Names].Pages[0], next.Data.Areas[Image.Names].Pages[0]);
        Assert.NotSame(prior.Data.Areas[Image.Metadata].Pages[0], next.Data.Areas[Image.Metadata].Pages[0]);
        Assert.Equal(245760, prior.Meta(prior.Find(9)).Size);
        Assert.Equal(777, next.Meta(next.Find(9)).Size);
        prior.Data.ValidateAll();
        next.Data.ValidateAll();
        for (var i = 0; i < 65; i++)
            writer.Apply([new(9, 7, "rapor.pdf", Fixture.Entry(9, 7, "rapor.pdf") with { SizeBytes = 1000 + i })], 2 + i);
        reader.Refresh();
        using var afterCheckpoint = reader.Acquire();
        Assert.Equal(1064, afterCheckpoint.Meta(afterCheckpoint.Find(9)).Size);
        Assert.Equal(777, next.Meta(next.Find(9)).Size);
        afterCheckpoint.Data.ValidateAll();
    }

    [Fact]
    public void SelectedRootWithUnpairedUtf16SurvivesReopen()
    {
        using var fixture = new Fixture();
        var root = "C:\\name\ud800";
        using (var catalog = Catalog.Create(fixture.Directory, 5, 1024, "volume", root))
        {
            catalog.Accept(Fixture.Entry(5, 5, "name\ud800", true));
            catalog.Accept(Fixture.Entry(6, 5, "report.txt"));
            catalog.Finish();
        }
        using var reopened = CatalogStore.Open(fixture.Directory);
        using var query = reopened.Query();
        Assert.Equal(root, query.GetRoots().Single().FullPath);
        Assert.True(query.ContainsPath(root + "\\report.txt"));
    }

    [Fact(Skip = "Token sistemi geçici olarak kapalı: TokenIndexing.Enabled=false")]
    public void StandardAndAdvancedSearchPreserveResultsAndSelectedRootName()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        var reader = new Reader(catalog);
        var tokenizer = new BasicTokenizer();
        var nodes = reader.GetAllItems().Select(item => new FileSystemNode(item.Name, item.FullPath, item.IsDirectory)
        {
            Metadata = item.IsDirectory ? null : new FileMetadata
            { SizeBytes = item.SizeBytes, CreatedTime = item.CreatedTime, LastWriteTime = item.LastWriteTime, OpenCount = item.OpenCount }
        }).ToArray();
        foreach (var node in nodes)
            nodes.FirstOrDefault(parent => parent.FullPath == Path.GetDirectoryName(node.FullPath))?.AddChild(node);
        var legacy = SearchState.Create(nodes, tokenizer);
        var expected = new SearchEngine(_ => legacy, tokenizer, new BasicScoringStrategy());
        var actual = new SearchEngine(_ => reader, tokenizer, new BasicScoringStrategy());
        foreach (var query in new[] { "ba", "rapor", "pdf", "ışık", "backup pdf", string.Join(' ', Enumerable.Repeat("pdf", 64)) })
            foreach (var sort in new[] { ResultView.SearchDefault, new ResultView(ItemFilter.None, new ItemSort(SortField.Name, false)) })
                AssertResults(expected.Search(query, 50, sort), actual.Search(query, 50, sort));
        Assert.Equal("Documents", reader.GetRoots().Single().Name);
        Assert.True(reader.ContainsPath(@"C:\Documents\Maya\rapor.pdf"));
        Assert.False(reader.ContainsPath(@"C:\Documents2\Maya\rapor.pdf"));
        Assert.Single(reader.GetChildren(@"C:\Documents\Maya"));
        var filter = new StructuredQuery { FilterOnlyMode = true };
        var expectedAdvanced = new AdvancedSearchEngine(_ => legacy, tokenizer, new BasicScoringStrategy());
        var actualAdvanced = new AdvancedSearchEngine(_ => reader, tokenizer, new BasicScoringStrategy());
        AssertResults(expectedAdvanced.Search(filter), actualAdvanced.Search(filter));
    }

    [Fact]
    public void UpdatesCompactionAndOwnerDisposalKeepAcquiredReadersValid()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        using var writer = new CatalogStore(catalog, writable: true);
        using var readerStore = CatalogStore.Open(fixture.Directory);
        using var old = readerStore.Query();
        writer.Apply([new Change(9, 7, "rapor.pdf", Fixture.Entry(9, 7, "yeni.pdf"))], 10);
        writer.Compact();
        readerStore.Refresh();
        using var current = readerStore.Query();
        readerStore.Dispose();
        writer.Dispose();
        Assert.True(old.ContainsPath(@"C:\Documents\Maya\rapor.pdf"));
        Assert.False(old.ContainsPath(@"C:\Documents\Maya\yeni.pdf"));
        Assert.True(current.ContainsPath(@"C:\Documents\Maya\yeni.pdf"));
        Assert.False(current.ContainsPath(@"C:\Documents\Maya\rapor.pdf"));
        using var reopened = CatalogStore.Open(fixture.Directory);
        using var disk = reopened.Query();
        Assert.True(disk.ContainsPath(@"C:\Documents\Maya\yeni.pdf"));
    }

    [Fact]
    public void ReadOnlyGateWorksAndOnlyOneWriterOwnsCompaction()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        using var writer = new CatalogStore(catalog, writable: true);
        Assert.Throws<IOException>(() => new CatalogStore(catalog, writable: true));
        var gate = Path.Combine(fixture.Directory, ".gate");
        File.SetAttributes(gate, FileAttributes.ReadOnly);
        try
        {
            using var reader = CatalogStore.Open(fixture.Directory);
            using var query = reader.Query();
            Assert.Equal(catalog.Data.EntryCount, query.ItemCount);
            Assert.Throws<InvalidOperationException>(() => reader.Apply([], 1));
        }
        finally { File.SetAttributes(gate, FileAttributes.Normal); }
    }

    [Fact]
    public void FailedMutationDoesNotPublishAnyPartOfTheBatch()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        using var writer = new CatalogStore(catalog, writable: true);
        Assert.Throws<InvalidDataException>(() => writer.Apply([
            new Change(9, 7, "rapor.pdf", Fixture.Entry(9, 7, "yeni.pdf")),
            new Change(10, 99, "missing.txt", Fixture.Entry(10, 99, "missing.txt"))], 1));
        using var reopened = CatalogStore.Open(fixture.Directory);
        using var query = reopened.Query();
        Assert.True(query.ContainsPath(@"C:\Documents\Maya\rapor.pdf"));
        Assert.False(query.ContainsPath(@"C:\Documents\Maya\yeni.pdf"));
    }

    private static void AssertResults(IReadOnlyList<SearchResult> expected, IReadOnlyList<SearchResult> actual) =>
        Assert.Equal(expected.Select(x => (x.FullPath, x.Score)), actual.Select(x => (x.FullPath, x.Score)));

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "OmniSpot.Paged.Tests", Guid.NewGuid().ToString("N"));
        internal Catalog Build(bool buildNameOrder = true)
        {
            var catalog = Catalog.Create(Directory, 5, 1024, "volume-test", @"C:\Documents");
            foreach (var entry in new[]
            {
                Entry(9, 7, "rapor.pdf"), Entry(7, 5, "Maya", true), Entry(5, 5, "Documents", true),
                Entry(10, 5, "backup.pdf"), Entry(11, 5, "ışık.txt")
            }) catalog.Accept(entry);
            catalog.Finish(buildNameOrder: buildNameOrder);
            return catalog;
        }
        internal static NtfsMftEntry Entry(ulong id, ulong parent, string name, bool directory = false) =>
            new(id, parent, name, directory, directory ? FileAttributes.Directory : FileAttributes.Archive,
                directory ? 0 : 245760, new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc).Ticks,
                new DateTime(2026, 9, 15, 10, 30, 0, DateTimeKind.Utc).Ticks);
        public void Dispose()
        {
            if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, recursive: true);
        }
    }
}
