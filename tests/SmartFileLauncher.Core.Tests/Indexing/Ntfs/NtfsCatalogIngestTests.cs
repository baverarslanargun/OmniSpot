using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search.Paged;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Indexing.Ntfs;

public sealed class NtfsCatalogIngestTests
{
    [Fact]
    public void WholeVolumePlacesChildrenBeforeParentsAndPreservesHardlinksAndMetadata()
    {
        var path = Temporary();
        try
        {
            using var catalog = Catalog.Create(path, 5, 1024);
            var ingest = new NtfsCatalogIngest([catalog]);
            var file = Entry(42, 7, "rapor.pdf") with { SizeBytes = 245760, Attributes = FileAttributes.Hidden };
            ingest.Accept(file);
            ingest.Accept(file with { ParentFileReference = 8, Name = "second.pdf" });
            ingest.Accept(Entry(8, 5, "private", true));
            ingest.Accept(Entry(7, 5, "docs", true));
            ingest.Accept(Entry(5, 5, ".", true));
            ingest.Complete(_ => throw new Xunit.Sdk.XunitException("No live lookup required"), default);
            catalog.Finish();
            Assert.Equal(5, catalog.Data.EntryCount);
            var reader = new Reader(catalog);
            var id = reader.FindPath(@"C:\docs\rapor.pdf");
            Assert.NotEqual(0u, id);
            Assert.Equal(245760, catalog.Meta(id).Size);
            Assert.Equal((uint)FileAttributes.Hidden, catalog.Meta(id).Attributes);
            Assert.NotEqual(id, reader.FindPath(@"C:\private\second.pdf"));
            Assert.Equal(42UL, catalog.Row(reader.FindPath(@"C:\private\second.pdf")).Frn);
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void SelectedScopeRoutesByIdentityWithoutIncludingOtherTrees()
    {
        var path = Temporary();
        try
        {
            using var catalog = Catalog.Create(path, 7, 1024, rootPath: @"C:\docs");
            var ingest = new NtfsCatalogIngest([catalog]);
            ingest.Accept(Entry(42, 9, "inside.txt"));
            ingest.Accept(Entry(42, 8, "outside.txt"));
            ingest.Accept(Entry(9, 7, "nested", true));
            ingest.Accept(Entry(8, 5, "other", true));
            ingest.Accept(Entry(7, 5, "docs", true));
            ingest.Accept(Entry(5, 5, ".", true));
            ingest.Complete(_ => throw new Xunit.Sdk.XunitException("No live lookup required"), default);
            catalog.Finish();
            Assert.Equal(3, catalog.Data.EntryCount);
            Assert.True(new Reader(catalog).ContainsPath(@"C:\docs\nested\inside.txt"));
            Assert.DoesNotContain(catalog.ActiveIds(), id => catalog.Name(id) == "outside.txt");
        }
        finally { Directory.Delete(path, true); }
    }

    [Fact]
    public void MissingParentUsesCurrentFullReferenceAndDeletedAliasIsRemoved()
    {
        var path = Temporary();
        try
        {
            using var catalog = Catalog.Create(path, 5, 1024);
            var ingest = new NtfsCatalogIngest([catalog]);
            ingest.Accept(Entry(5, 5, ".", true));
            ingest.Accept(Entry(42, (2UL << 48) | 7, "gone.txt"));
            var reads = new List<ulong>();
            ingest.Complete(reference => { reads.Add(reference); return []; }, default);
            catalog.Finish();
            Assert.Contains((2UL << 48) | 7, reads);
            Assert.Contains(42UL, reads);
            Assert.Equal(1, catalog.Data.EntryCount);
        }
        finally { Directory.Delete(path, true); }
    }

    private static string Temporary() => Path.Combine(Path.GetTempPath(), "OmniSpot.Ingest." + Guid.NewGuid().ToString("N"));
    private static NtfsMftEntry Entry(ulong frn, ulong parent, string name, bool directory = false) =>
        new(frn, parent, name, directory, directory ? FileAttributes.Directory : FileAttributes.Archive,
            123, new DateTime(2026, 9, 15, 9, 0, 0, DateTimeKind.Utc).Ticks, new DateTime(2026, 9, 15, 10, 30, 0, DateTimeKind.Utc).Ticks);
}
