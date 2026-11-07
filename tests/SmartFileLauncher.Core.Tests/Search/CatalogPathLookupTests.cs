using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search.Paged;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class CatalogPathLookupTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LookupTracksRenameMoveDeleteReuseAndHardLinks(bool cache)
    {
        using var fixture = new Fixture();
        using var catalog = Catalog.Create(fixture.Path, 5, 1024);
        catalog.Accept(Entry(5, 5, "C:\\", true));
        catalog.Accept(Entry(6, 5, "Alpha", true));
        catalog.Accept(Entry(7, 5, "Beta", true));
        var file = catalog.Accept(Entry(8, 6, "item.txt"));
        var link = catalog.Accept(Entry(8, 7, "link.txt"));
        var reader = new Reader(catalog, cachePaths: cache);
        Assert.Equal(file, reader.FindPath(@"C:\ALPHA\ITEM.TXT"));
        Assert.Equal(link, reader.FindPath(@"C:\Beta\link.txt"));
        catalog.Apply(new(6, 5, "Alpha", Entry(6, 5, "Renamed", true)));
        Assert.Equal(0u, reader.FindPath(@"C:\Alpha\item.txt"));
        Assert.Equal(file, reader.FindPath(@"C:\Renamed\item.txt"));
        catalog.Apply(new(8, 6, "item.txt", Entry(8, 7, "moved.txt")));
        Assert.Equal(0u, reader.FindPath(@"C:\Renamed\item.txt"));
        Assert.Equal(file, reader.FindPath(@"C:\Beta\moved.txt"));
        catalog.Apply(new(8, 7, "moved.txt", null));
        var reused = catalog.Accept(Entry(9, 6, "replacement.txt"));
        Assert.Equal(file, reused);
        Assert.Equal(0u, reader.FindPath(@"C:\Beta\moved.txt"));
        Assert.Equal(reused, reader.FindPath(@"C:\Renamed\replacement.txt"));
        Assert.Equal(link, reader.FindPath(@"C:\Beta\link.txt"));
        catalog.Apply(new(6, 5, "Renamed", null));
        Assert.Equal(0u, reader.FindPath(@"C:\Renamed\replacement.txt"));
        var newDirectory = catalog.Accept(Entry(20, 5, "Renamed", true));
        var replacement = catalog.Accept(Entry(21, 20, "replacement.txt"));
        Assert.Equal(newDirectory, reader.FindPath(@"C:\Renamed"));
        Assert.Equal(replacement, reader.FindPath(@"C:\Renamed\replacement.txt"));
        Assert.Equal(0u, reader.FindPath(@"C:\Beta\link.txt\child"));
        Assert.Equal(0u, reader.FindPath(@"D:\Renamed\replacement.txt"));
        catalog.Audit();
    }

    [Fact]
    public void RepeatedLookupInLargeDirectoryDoesNotMaterializeSiblings()
    {
        using var fixture = new Fixture();
        using var catalog = Catalog.Create(fixture.Path, 5, 20000);
        catalog.Accept(Entry(5, 5, "C:\\", true));
        catalog.Accept(Entry(6, 5, "Large", true));
        uint last = 0;
        for (ulong i = 10; i < 20010; i++) last = catalog.Accept(Entry(i, 6, $"file-{i}.txt"));
        var reader = new Reader(catalog, cachePaths: true);
        Assert.Equal(last, reader.FindPath(@"C:\Large\file-20009.txt"));
        var before = GC.GetAllocatedBytesForCurrentThread();
        for (var i = 0; i < 100; i++) Assert.Equal(last, reader.FindPath(@"C:\Large\file-20009.txt"));
        var allocated = GC.GetAllocatedBytesForCurrentThread() - before;
        Assert.True(allocated < 256 * 1024, $"Yol çözümleme gereksiz kardeş kayıtları ayırdı: {allocated:N0} bayt.");
        Assert.Equal(0u, reader.FindPath(@"C:\Large\not-yet.txt"));
        var added = catalog.Accept(Entry(30000, 6, "not-yet.txt"));
        Assert.Equal(added, reader.FindPath(@"C:\Large\not-yet.txt"));
    }

    [Fact]
    public void MissingSubtreeCacheTracksCreationRenameAndReplacement()
    {
        using var fixture = new Fixture();
        using var catalog = Catalog.Create(fixture.Path, 5, 1024);
        catalog.Accept(Entry(5, 5, "C:\\", true));
        var reader = new Reader(catalog, cachePaths: true);
        Assert.Equal(0u, reader.FindPath(@"C:\Missing\child.txt"));
        Assert.Equal(0u, reader.FindPath(@"C:\Missing\other.txt"));
        catalog.Accept(Entry(6, 5, "Missing", true));
        var child = catalog.Accept(Entry(7, 6, "child.txt"));
        Assert.Equal(child, reader.FindPath(@"C:\Missing\child.txt"));
        Assert.Equal(0u, reader.FindPath(@"C:\Renamed\child.txt"));
        catalog.Apply(new(6, 5, "Missing", Entry(6, 5, "Renamed", true)));
        Assert.Equal(child, reader.FindPath(@"C:\Renamed\child.txt"));
        Assert.Equal(0u, reader.FindPath(@"C:\Missing\child.txt"));
        catalog.RemoveVerifiedAlias(reader.FindPath(@"C:\Renamed"));
        catalog.Accept(Entry(8, 5, "Missing", true));
        var replacement = catalog.Accept(Entry(9, 8, "child.txt"));
        Assert.Equal(replacement, reader.FindPath(@"C:\Missing\child.txt"));
        catalog.Audit();
    }

    private static NtfsMftEntry Entry(ulong frn, ulong parent, string name, bool dir = false) =>
        new(frn, parent, name, dir, dir ? FileAttributes.Directory : FileAttributes.Normal,
            1, DateTime.UnixEpoch.Ticks, DateTime.UnixEpoch.Ticks);
    private sealed class Fixture : IDisposable
    {
        internal string Path { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "OmniSpot.PathLookup." + Guid.NewGuid().ToString("N"));
        public void Dispose() { if (Directory.Exists(Path)) Directory.Delete(Path, true); }
    }
}
