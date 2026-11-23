using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Search.Paged;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class FolderScopeSearchTests
{
    private const ulong Root = 5;

    [Theory]
    [InlineData(@"C:\Projeler", @"C:\Projeler\a.txt", true)]
    [InlineData(@"C:\Projeler", @"C:\PROJELER\Alt\b.txt", true)]
    [InlineData(@"C:\Projeler\", @"C:\Projeler\a.txt", true)]
    [InlineData(@"C:\Projeler", @"C:\Projeler", false)]
    [InlineData(@"C:\Projeler", @"C:\Projeler\", false)]
    [InlineData(@"C:\Projeler", @"C:\Projeler2\a.txt", false)]
    [InlineData(@"C:\Projeler", @"C:\Belgeler\a.txt", false)]
    [InlineData(@"C:\", @"C:\a.txt", true)]
    [InlineData(@"C:\", @"D:\a.txt", false)]
    public void PathScopeKeepsOnlyStrictDescendants(string scope, string path, bool expected) =>
        Assert.Equal(expected, PathScope.Contains(scope, path));

    [Fact]
    public void RequestNormalizesTheScopeAndUnscopedRequestsStayUnchanged()
    {
        Assert.Equal(@"C:\Projeler", new SearchRequest("a", false, true, Scope: @"C:\Projeler\").View.Scope);
        Assert.Null(new SearchRequest("a", false, true, Scope: "  ").View.Scope);
        Assert.Equal(ResultView.SearchDefault, new SearchRequest("a", false, true).View);
        Assert.NotEqual(new SearchRequest("a", false, true), new SearchRequest("a", false, true, Scope: @"C:\Projeler"));
    }

    [Fact]
    public void MftSearchAndCountStayInsideTheFolderAcrossSortsFiltersRankingAndPages()
    {
        using var index = BuildIndex();
        var scoped = ResultView.SearchDefault with { Scope = @"C:\Projeler" };
        var views = new[]
        {
            scoped,
            scoped with { Sort = ItemSort.NameAscending },
            scoped with { Sort = new ItemSort(SortField.Size, Descending: true) },
            scoped with { Filter = new ItemFilter(Kind: FilterItemKind.FilesOnly) },
            scoped with { LiteralRanking = new LiteralRanking([(@"C:\Belgeler\fatura-elektrik.pdf", 0.9)]) }
        };

        foreach (var view in views)
        {
            var all = index.Search("fatura", 0, 500, view, null);
            Assert.NotEmpty(all);
            Assert.All(all, hit => Assert.StartsWith(@"C:\Projeler\", hit.FullPath));
            Assert.Contains(all, hit => hit.FullPath == @"C:\Projeler\Muhasebe\Faturalar\fatura-ekim.pdf");
            Assert.DoesNotContain(all, hit => hit.FullPath.StartsWith(@"C:\Projeler2", StringComparison.OrdinalIgnoreCase));
            Assert.Equal(all.Count, index.CountMatches("fatura", view, null));
            var paged = index.Search("fatura", 0, 7, view, null).Concat(index.Search("fatura", 7, 500, view, null));
            Assert.Equal(all.Select(hit => hit.FullPath), paged.Select(hit => hit.FullPath));
        }

        var everywhere = index.Search("fatura", 0, 500, ResultView.SearchDefault, null);
        Assert.Contains(everywhere, hit => hit.FullPath == @"C:\Belgeler\fatura-elektrik.pdf");
        Assert.Contains(everywhere, hit => hit.FullPath == @"C:\Projeler2\fatura-eski.pdf");
    }

    [Fact]
    public void MftScopeNeverReturnsTheScopeFolderItself()
    {
        using var index = BuildIndex();
        var view = ResultView.SearchDefault with { Scope = @"C:\Projeler\Muhasebe\Faturalar" };
        var hits = index.Search("fatura", 0, 100, view, null).Select(hit => hit.FullPath).ToArray();
        Assert.Equal(new[] { @"C:\Projeler\Muhasebe\Faturalar\fatura-ekim.pdf", @"C:\Projeler\Muhasebe\Faturalar\fatura-kasim.pdf" },
            hits.Order(StringComparer.OrdinalIgnoreCase));
        Assert.Equal(2, index.CountMatches("fatura", view, null));
        Assert.Contains(index.Search("faturalar", 0, 100, ResultView.SearchDefault, null),
            hit => hit.FullPath == @"C:\Projeler\Muhasebe\Faturalar");
        Assert.Empty(index.Search("faturalar", 0, 100, view, null));
    }

    [Fact]
    public void MftScopeIntersectsWithTheVolumeScope()
    {
        using var index = BuildIndex();
        var inside = ResultView.SearchDefault with { Scope = @"C:\Projeler\Muhasebe" };
        Assert.All(index.Search("fatura", 0, 100, inside, [@"C:\Projeler"]),
            hit => Assert.StartsWith(@"C:\Projeler\Muhasebe\", hit.FullPath));
        Assert.Equal(3, index.CountMatches("fatura", inside, [@"C:\Projeler"]));

        var above = ResultView.SearchDefault with { Scope = @"C:\" };
        Assert.Equal(index.Search("fatura", 0, 100, ResultView.SearchDefault, [@"C:\Projeler"]).Select(hit => hit.FullPath),
            index.Search("fatura", 0, 100, above, [@"C:\Projeler"]).Select(hit => hit.FullPath));

        var outside = ResultView.SearchDefault with { Scope = @"C:\Belgeler" };
        Assert.Empty(index.Search("fatura", 0, 100, outside, [@"C:\Projeler"]));
        Assert.Equal(0, index.CountMatches("fatura", outside, [@"C:\Projeler"]));
    }

    [Theory]
    [InlineData(@"C:\Yok")]
    [InlineData(@"C:\Projeler\Muhasebe\butce-2026.xlsx")]
    [InlineData(@"D:\Projeler")]
    public void MftScopeOnAMissingFolderOrAFileFindsNothing(string scope)
    {
        using var index = BuildIndex();
        var view = ResultView.SearchDefault with { Scope = scope };
        Assert.Empty(index.Search("a", 0, 100, view, null));
        Assert.Equal(0, index.CountMatches("a", view, null));
    }

    [Fact]
    public void LiteralSearchThroughTheReaderAppliesTheRequestScopeToPagesAndCounts()
    {
        using var index = BuildIndex();
        var engine = new SearchEngine(_ => new FileIndexReader([(index, null)]), new BasicTokenizer(),
            new BasicScoringStrategy(), literalNamesOnly: true);
        var view = new SearchRequest("fatura", false, true, Scope: @"C:\Projeler\").View;

        var first = engine.Search("fatura", 100, view);
        Assert.Equal(4, first.Count);
        Assert.All(first, hit => Assert.StartsWith(@"C:\Projeler\", hit.FullPath));
        Assert.Equal(4, engine.CountMatches("fatura", view));
        Assert.Equal(first.Skip(1).Select(hit => hit.FullPath), engine.SearchPage("fatura", 1, 10, view).Select(hit => hit.FullPath));
        Assert.Equal(6, engine.Search("fatura", 100, ResultView.SearchDefault).Count);
    }

    [Fact]
    public void AiSearchHonoursTheScopeForKeywordAndFilterOnlyQueries()
    {
        using var index = BuildIndex();
        var engine = new AdvancedSearchEngine(_ => new FileIndexReader([(index, null)]), new BasicTokenizer(), new BasicScoringStrategy());
        var view = ResultView.SearchDefault with { Scope = @"C:\Projeler" };

        var keyword = engine.Search(new StructuredQuery { Keywords = ["fatura"] }, 50, view);
        Assert.NotEmpty(keyword);
        Assert.All(keyword, hit => Assert.StartsWith(@"C:\Projeler\", hit.FullPath));
        Assert.Contains(engine.Search(new StructuredQuery { Keywords = ["fatura"] }, 50, ResultView.SearchDefault),
            hit => hit.FullPath.StartsWith(@"C:\Belgeler\", StringComparison.Ordinal));

        var filterOnly = engine.Search(new StructuredQuery { FilterOnlyMode = true, HardExtensions = ["pdf"] }, 50, view);
        Assert.NotEmpty(filterOnly);
        Assert.All(filterOnly, hit => Assert.StartsWith(@"C:\Projeler\", hit.FullPath));
    }

    [Fact]
    public void PagedLiteralSearchStaysInsideTheFolderForEveryPathAndSort()
    {
        using var fixture = new PagedFixture();
        using var catalog = fixture.Build();
        var scope = @"C:\Kok\Projeler";
        var views = new[]
        {
            ResultView.SearchDefault,
            ResultView.SearchDefault with { Sort = ItemSort.NameAscending },
            ResultView.SearchDefault with { Sort = new ItemSort(SortField.Modified, Descending: true) },
            ResultView.SearchDefault with { Filter = new ItemFilter(Kind: FilterItemKind.FilesOnly) },
            ResultView.SearchDefault with { LiteralRanking = new LiteralRanking([(@"C:\Kok\Belgeler\fatura-elektrik.pdf", 0.9)]) }
        };

        void Check()
        {
            foreach (var view in views)
            {
                var scoped = catalog.SearchLiteralNames("fatura", 100, view with { Scope = scope }, default);
                Assert.NotEmpty(scoped);
                Assert.All(scoped, hit => Assert.StartsWith(scope + @"\", hit.FullPath));
                Assert.DoesNotContain(scoped, hit => hit.FullPath.Equals(scope, StringComparison.OrdinalIgnoreCase));
                var expected = catalog.SearchLiteralNames("fatura", 1000, view, default)
                    .Where(hit => hit.FullPath.StartsWith(scope + @"\", StringComparison.OrdinalIgnoreCase))
                    .Select(hit => hit.FullPath).Order(StringComparer.OrdinalIgnoreCase);
                Assert.Equal(expected, scoped.Select(hit => hit.FullPath).Order(StringComparer.OrdinalIgnoreCase));
            }
        }

        Check();
        catalog.PrepareLiteralNameIndex(default);
        Check();
    }

    [Fact]
    public void PagedScopeAtOrAboveTheCatalogRootIsUnrestrictedAndOutsideItIsEmpty()
    {
        using var fixture = new PagedFixture();
        using var catalog = fixture.Build();
        var all = catalog.SearchLiteralNames("fatura", 100, ResultView.SearchDefault, default).Select(hit => hit.FullPath).ToArray();
        Assert.Equal(all, catalog.SearchLiteralNames("fatura", 100, ResultView.SearchDefault with { Scope = @"C:\Kok" }, default)
            .Select(hit => hit.FullPath));
        Assert.Equal(all, catalog.SearchLiteralNames("fatura", 100, ResultView.SearchDefault with { Scope = @"C:\" }, default)
            .Select(hit => hit.FullPath));
        Assert.Empty(catalog.SearchLiteralNames("fatura", 100, ResultView.SearchDefault with { Scope = @"D:\Kok" }, default));
        Assert.Empty(catalog.SearchLiteralNames("fatura", 100, ResultView.SearchDefault with { Scope = @"C:\Kok\Yok" }, default));
        Assert.Empty(catalog.SearchLiteralNames("fatura", 100,
            ResultView.SearchDefault with { Scope = @"C:\Kok\Projeler\fatura-ekim.pdf" }, default));
    }

    private static FileIndex BuildIndex() => FileIndex.Build(@"C:\", Root,
    [
        Folder(Root, Root, @"C:\"),
        Folder(100, Root, "Projeler"),
        Folder(101, 100, "Muhasebe"),
        Folder(102, 101, "Faturalar"),
        File(103, 102, "fatura-ekim.pdf", 300),
        File(104, 102, "fatura-kasim.pdf", 200),
        File(105, 101, "butce-2026.xlsx", 100),
        File(106, 100, "eski-fatura.txt", 50),
        Folder(200, Root, "Belgeler"),
        File(201, 200, "fatura-elektrik.pdf", 400),
        Folder(300, Root, "Projeler2"),
        File(301, 300, "fatura-eski.pdf", 500)
    ]);

    private static IndexEntry Folder(ulong id, ulong parent, string name) =>
        new(id, parent, name, FileAttributes.Directory, 0, DateTime.UnixEpoch.Ticks);

    private static IndexEntry File(ulong id, ulong parent, string name, long size) =>
        new(id, parent, name, FileAttributes.Archive, size, DateTime.UtcNow.AddDays(-(long)id).Ticks);

    private sealed class PagedFixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "OmniSpot.FolderScope.Tests", Guid.NewGuid().ToString("N"));

        internal Catalog Build()
        {
            var catalog = Catalog.Create(_directory, 5, 4096, "folder-scope-test", @"C:\Kok");
            catalog.Accept(new NtfsMftEntry(5, 5, "Kok", true, FileAttributes.Directory, 0, 0, 0));
            catalog.Accept(Directory(6, 5, "Projeler"));
            catalog.Accept(Directory(7, 6, "Muhasebe"));
            catalog.Accept(Directory(8, 5, "Belgeler"));
            catalog.Accept(Directory(9, 5, "Projeler2"));
            catalog.Accept(Directory(10, 6, "Faturalar"));
            catalog.Accept(Entry(20, 7, "fatura-ekim.pdf"));
            catalog.Accept(Entry(21, 10, "fatura-kasim.pdf"));
            catalog.Accept(Entry(22, 6, "fatura-ekim.pdf"));
            catalog.Accept(Entry(23, 8, "fatura-elektrik.pdf"));
            catalog.Accept(Entry(24, 9, "fatura-eski.pdf"));
            for (var index = 0; index < 300; index++)
                catalog.Accept(Entry((ulong)(100 + index), index < 20 ? 7UL : 8UL, $"fatura-{index:000}.txt"));
            catalog.Finish(buildNameOrder: false);
            return catalog;
        }

        private static NtfsMftEntry Directory(ulong id, ulong parent, string name) =>
            new(id, parent, name, true, FileAttributes.Directory, 0, 0, 0);

        private static NtfsMftEntry Entry(ulong id, ulong parent, string name) => new(
            id, parent, name, false, FileAttributes.Archive, checked((long)id * 1024),
            DateTime.UtcNow.AddDays(-(long)(id % 30)).Ticks, DateTime.UtcNow.AddDays(-(long)(id % 30)).Ticks);

        public void Dispose()
        {
            try { System.IO.Directory.Delete(_directory, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
