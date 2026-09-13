using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using SmartFileLauncher.Core.Tests.Indexing.Ntfs;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class LiveCatalogTests
{
    [Fact]
    public void FilesAreSearchableBeforeDirectoriesAndOldViewsStayUnchanged()
    {
        using var workspace = new TemporaryDirectory();
        var records = PackedCatalogTests.Records();
        var path = Path.Combine(workspace.Path, "live");
        using var live = new LiveCatalog(path, @"C:\Root");
        var file = records[2];
        var fileId = live.Add(file);
        var old = live.Snapshot();
        Assert.Equal(file, old.GetRecord(fileId));
        Assert.Single(old.Get("rapor"));
        Assert.Equal(3, old.StructuralNodeCount);
        Assert.False(old.ContainsPath(records[1].Item.FullPath));
        foreach (var record in records.Where(record => record != file).Reverse()) live.Add(record);
        var current = live.Snapshot();
        Assert.Single(old.Get("rapor")); Assert.False(old.ContainsPath(records[1].Item.FullPath));
        Assert.Equal(records.Length, current.ItemCount);
        Assert.Equal(fileId, live.Add(file));
        live.Seal();
        Assert.Single(old.Get("rapor"));
        using var reopened = LiveCatalog.Open(path);
        var expected = CompactSearchState.Create(records.Select(record => record.Item), new BasicTokenizer());
        foreach (var state in new[] { current, reopened.Snapshot() })
        {
            foreach (var record in records) Assert.Equal(record, state.FindRecord(record.Item.FullPath));
            foreach (var query in new[] { "rapor", "txt", "RAP", "İş", "📁", "case", "", "\ud800", "rappr" })
            {
                Assert.Equal(Order(expected.Get(query)), Order(state.Get(query)));
                Assert.Equal(Order(expected.GetPartial(query)), Order(state.GetPartial(query)));
                Assert.Equal(Order(expected.GetFuzzy(query, 1)), Order(state.GetFuzzy(query, 1)));
            }
            Assert.Equal(Order(expected.GetAllItems()), Order(state.GetAllItems()));
            Assert.Equal(Order(expected.GetDescendants(records[0].Item)), Order(state.GetDescendants(records[0].Item)));
        }
    }

    [Fact]
    public void MftChildBeforeParentIsNameIndexedAndResolvesWithoutReinsertingChild()
    {
        using var workspace = new TemporaryDirectory();
        using var live = new LiveCatalog(Path.Combine(workspace.Path, "mft"), @"C:\Root");
        var input = new LiveMftIngestor(live, 5);
        var ticks = new DateTime(2026, 9, 13, 0, 0, 0, DateTimeKind.Utc).Ticks;
        var first = new NtfsMftEntry(40, 20, "rapor.txt", false, FileAttributes.Normal, 123, ticks, ticks);
        var id = input.Accept(first);
        var unresolved = live.Snapshot();
        Assert.Equal(0, unresolved.ItemCount); Assert.Equal(1, unresolved.AcceptedItemCount); Assert.Equal(1, unresolved.PendingItemCount);
        Assert.Equal(id, Assert.Single(unresolved.GetPending("rapor")).Id);
        Assert.Empty(unresolved.Get("rapor"));
        input.Accept(new(20, 5, "Root", true, FileAttributes.Directory, 0, ticks, ticks));
        var resolved = live.Snapshot();
        Assert.Equal(@"C:\Root\Root\rapor.txt", Assert.Single(resolved.Get("rapor")).FullPath);
        Assert.Equal(id, Assert.Single(unresolved.GetPending("rapor")).Id);
        Assert.Empty(unresolved.Get("rapor"));
        var linkId = input.Accept(first with { ParentFileReference = 5, Name = "link.txt" });
        Assert.NotEqual(id, linkId);
        input.Accept(new(5, 5, "Root", true, FileAttributes.Directory, 0, ticks, ticks));
        Assert.Equal(4, live.Snapshot().ItemCount);
        live.Seal();
    }

    [Fact]
    public void ActualMftParserAndReaderFeedLiveCatalogBeforeParentRecordArrives()
    {
        using var workspace = new TemporaryDirectory();
        using var live = new LiveCatalog(Path.Combine(workspace.Path, "raw"), @"C:\Root");
        var source = new FakeNtfsVolumeSource { Geometry = new(512, 512, 1024, 128, 8192) };
        source.Add(0, NtfsMftTestData.Record(NtfsMftTestData.Basic(), NtfsMftTestData.Name("$MFT"),
            NtfsMftTestData.Nonresident(0x80, 8192, 0, 15, 0x11, 16, 8, 0)), 4096);
        source.Add(1, NtfsMftTestData.Record(NtfsMftTestData.Basic(FileAttributes.Hidden | FileAttributes.System),
            NtfsMftTestData.Name("child.txt", 6), NtfsMftTestData.Name("link.txt", 5), NtfsMftTestData.Resident(0x80, [1, 2, 3])), 5120);
        source.Add(5, NtfsMftTestData.Record(true, 0, NtfsMftTestData.Basic(FileAttributes.Directory), NtfsMftTestData.Name("Root")), 9216);
        source.Add(6, NtfsMftTestData.Record(true, 0, NtfsMftTestData.Basic(FileAttributes.Directory), NtfsMftTestData.Name("Folder")), 10240);
        var sink = new LiveMftIngestor(live, NtfsMftTestData.Reference(5));
        using var reader = new NtfsMftReader(source);
        foreach (var entry in reader.ReadEntries())
        {
            sink.Accept(entry);
            if (entry.Name == "child.txt") Assert.Single(live.Snapshot().GetPending("child"));
        }
        var record = live.Snapshot().FindRecord(@"C:\Root\Folder\child.txt")!;
        Assert.Equal(3, record.Item.SizeBytes); Assert.True(record.Hidden); Assert.True(record.System);
        Assert.Equal(NtfsMftTestData.Created.Ticks, record.CreatedUtc); Assert.Equal(NtfsMftTestData.Modified.Ticks, record.ModifiedUtc);
        Assert.Equal(NtfsMftTestData.Created.ToLocalTime(), record.Item.CreatedTime);
        Assert.Equal(NtfsMftTestData.Modified.ToLocalTime(), record.Item.LastWriteTime);
        Assert.True(record.IndexedUtc > 0); Assert.Equal(5, live.Snapshot().ItemCount);
        Assert.Single(live.Snapshot().Get("link")); live.Seal();
    }

    [Fact]
    public void MftVolumeRootUsesDisplayNameAndFullPathFromItsAnchor()
    {
        using var workspace = new TemporaryDirectory(); var path = Path.Combine(workspace.Path, "volume");
        using var live = new LiveCatalog(path, @"C:\");
        var sink = new LiveMftIngestor(live, 5);
        var ticks = NtfsMftTestData.Created.Ticks;
        sink.Accept(new(5, 5, ".", true, FileAttributes.Directory, 0, ticks, ticks));
        sink.Accept(new(6, 5, "file.txt", false, FileAttributes.Normal, 4, ticks, ticks));
        live.Seal();
        using var reopened = LiveCatalog.Open(path);
        foreach (var state in new[] { live.Snapshot(), reopened.Snapshot() })
        {
            var root = state.GetRecord(1)!;
            Assert.Equal(@"C:\", root.Item.Name); Assert.Equal(@"C:\", root.Item.FullPath);
            Assert.Equal("", root.Item.ParentPath);
            Assert.Equal(@"C:\file.txt", Assert.Single(state.Get("file")).FullPath);
        }
    }

    [Fact]
    public async Task PartialWriteIsNotObservableAndQueryStampSurvivesConcurrentPublication()
    {
        using var workspace = new TemporaryDirectory();
        using var live = new LiveCatalog(Path.Combine(workspace.Path, "race"), @"C:\Root");
        var records = PackedCatalogTests.Records(); live.Add(records[0]); live.Add(records[2]);
        var old = live.Snapshot();
        using var entered = new ManualResetEventSlim(); using var release = new ManualResetEventSlim();
        live.BeforePublish = () => { entered.Set(); if (!release.Wait(TimeSpan.FromSeconds(10))) throw new TimeoutException(); };
        var writing = Task.Run(() => live.Add(records[3]));
        Assert.True(entered.Wait(TimeSpan.FromSeconds(10)));
        var reading = Task.Run(() => old.Get("rapor"));
        release.Set(); await writing;
        Assert.Single(await reading); Assert.Equal(2, live.Snapshot().Get("rapor").Count);
    }

    [Fact]
    public void GrowingHashesPostingBlocksAndPageCrossingsRemainReadableAfterSeal()
    {
        using var workspace = new TemporaryDirectory(); var path = Path.Combine(workspace.Path, "pages");
        var root = PackedCatalogTests.Records()[0];
        using var live = new LiveCatalog(path, root.Item.FullPath);
        var records = new List<PackedRecord> { root }; live.Add(root);
        for (var i = 0; i < 5000; i++)
        {
            var name = $"document-{i}-" + new string('界', 80) + ".txt";
            var record = new PackedRecord(new(name, root.Item.FullPath + "\\" + name, false, i, null, null, 0, root.Item.FullPath), 0, 0, false, false, root.IndexedUtc);
            records.Add(record); live.Add(record);
        }
        var view = live.Snapshot(); Assert.Equal(5000, view.Get("document").Count);
        live.Seal();
        using var reopened = LiveCatalog.Open(path);
        foreach (var record in records) Assert.Equal(record, reopened.Snapshot().FindRecord(record.Item.FullPath));
        Assert.Equal(Order(view.GetPartial("doc")), Order(reopened.Snapshot().GetPartial("doc")));
    }

    [Fact]
    public void UnsealedOrDamagedCatalogCannotBeOpenedAsComplete()
    {
        using var workspace = new TemporaryDirectory(); var path = Path.Combine(workspace.Path, "partial");
        using (var live = new LiveCatalog(path, @"C:\Root"))
        {
            live.Add(PackedCatalogTests.Records()[2]);
            Assert.Throws<FileNotFoundException>(() => LiveCatalog.Open(path));
            Assert.Throws<InvalidDataException>(() => live.Seal());
        }
        var other = Path.Combine(workspace.Path, "complete");
        using (var live = new LiveCatalog(other, @"C:\Root")) { live.Add(PackedCatalogTests.Records()[0]); live.Seal(); }
        var file = Path.Combine(other, "names-000000.page");
        using (var output = new FileStream(file, FileMode.Open, FileAccess.Write)) { output.Position = 2; output.WriteByte(255); }
        Assert.Throws<InvalidDataException>(() => LiveCatalog.Open(other));
    }
    private static SearchItem[] Order(IEnumerable<SearchItem> items) => items.OrderBy(item => item.FullPath, StringComparer.Ordinal).ToArray();
}
