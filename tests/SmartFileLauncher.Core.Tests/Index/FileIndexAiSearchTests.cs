using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class FileIndexAiSearchTests
{
    private static readonly ulong Root = 5UL << 48 | 5;

    private static ulong Reference(ulong record) => 1UL << 48 | record;

    private static FileIndex Build()
    {
        var recent = DateTime.UtcNow.AddDays(-10).Ticks;
        var old = DateTime.UtcNow.AddDays(-400).Ticks;
        IndexEntry File(ulong record, string name, long created) =>
            new(Reference(record), Reference(20), name, FileAttributes.Archive, 2048, created, created);
        return FileIndex.Build(@"C:\Kok", Root,
        [
            new IndexEntry(Root, Root, ".", FileAttributes.Directory, 0, old, old),
            new IndexEntry(Reference(20), Root, "Belgeler", FileAttributes.Directory, 0, old, old),
            File(21, "fatura_mart.pdf", recent),
            File(22, "efatura2024.pdf", recent),
            File(23, "Işık raporu.docx", old),
            File(24, "notlar.txt", old)
        ]);
    }

    private static AdvancedSearchEngine Engine(FileIndex index) =>
        new(_ => new FileIndexReader([(index, null)]), new BasicTokenizer(), new BasicScoringStrategy());

    private static string[] Names(IReadOnlyList<SearchResult> results) => results.Select(result => result.Name).ToArray();

    [Fact]
    public void AiSearchFindsWholeWordsPartsTurkishLettersAndTypos()
    {
        using var index = Build();
        var engine = Engine(index);

        var fatura = Names(engine.Search(new StructuredQuery { Keywords = ["fatura"] }));
        Assert.Contains("fatura_mart.pdf", fatura);
        Assert.Contains("efatura2024.pdf", fatura);
        Assert.DoesNotContain("notlar.txt", fatura);

        Assert.Contains("Işık raporu.docx", Names(engine.Search(new StructuredQuery { Keywords = ["isik"] })));

        var typo = Names(engine.Search(new StructuredQuery { Keywords = ["faturs"] }));
        Assert.Contains("fatura_mart.pdf", typo);
        Assert.DoesNotContain("notlar.txt", typo);
    }

    [Fact]
    public void KeywordSearchGivesTheSameResultsWithOneCombinedPassAsWithSeparateCalls()
    {
        string[] names =
        [
            "fatura_mart.pdf", "efatura2024.pdf", "faturalar-nisan.pdf", "fatura", "rapor.docx", "raporlar_2024.xlsx",
            "mart_raporu.pdf", "Işık raporu.docx", "isik-notlari.txt", "notlar.txt", "nots.md", "x1", "x10.bin", "rapor 2024 (kopya).pdf"
        ];
        var time = DateTime.UtcNow.AddDays(-3).Ticks;
        var entries = new List<IndexEntry> { new(Root, Root, ".", FileAttributes.Directory, 0, time, time) };
        for (var folder = 0UL; folder < 6; folder++)
        {
            entries.Add(new(Reference(100 + folder), Root, $"Klasor {folder} rapor", FileAttributes.Directory, 0, time, time));
            for (var file = 0; file < names.Length; file++)
                entries.Add(new(Reference(1000 + folder * 100 + (ulong)file), Reference(100 + folder),
                    folder % 2 == 0 ? names[file] : names[file].ToUpperInvariant(), FileAttributes.Archive, 100 + file, time, time));
        }
        using var index = FileIndex.Build(@"C:\Kok", Root, entries);
        var combined = Engine(index);
        var separate = new AdvancedSearchEngine(_ => new SeparateCalls(new FileIndexReader([(index, null)])), new BasicTokenizer(),
            new BasicScoringStrategy());
        var compared = 0;
        foreach (var keywords in new List<string>[] { ["fatura"], ["rapor"], ["fatura", "mart"], ["isik"], ["efatura2024"], ["nots"],
                     ["x1"], ["rapor", "2024"], ["faturs"], ["notlar", "rapor"], ["klasor"] })
        {
            var query = new StructuredQuery { Keywords = keywords };
            var expected = separate.Search(query, 500).Select(result => (result.FullPath, result.Score)).ToArray();
            var actual = combined.Search(query, 500).Select(result => (result.FullPath, result.Score)).ToArray();
            Assert.Equal(expected, actual);
            compared += expected.Length;
        }
        Assert.True(compared > 100, $"Karşılaştırılan sonuç çok az: {compared}");
    }

    [Fact]
    public void TokenLookupSkipsUnrequestedTypesBeforeConstructingCandidates()
    {
        using var index = FileIndex.Build(@"C:\Kok", Root,
        [
            new(Root, Root, ".", FileAttributes.Directory, 0, 0),
            new(Reference(20), Root, "proje", FileAttributes.Directory, 0, 0),
            new(Reference(21), Reference(20), "proje-kapak.PNG", FileAttributes.Archive, 128, 0),
            new(Reference(22), Reference(20), "proje.ä", FileAttributes.Archive, 128, 0),
            new(Reference(23), Reference(20), "proje.txt", FileAttributes.Archive, 128, 0)
        ]);
        var filter = new QueryCatalogFilter(true, true, false, false, null, null, null, null, null, null)
        {
            AllowedExtensions = new HashSet<string>([".png", ".Ä"], StringComparer.OrdinalIgnoreCase),
            IncludeDirectoriesWithoutExtension = true
        };

        var results = index.TokenItemsWithPartial("proje", null, filter, out var found, CancellationToken.None);

        Assert.True(found);
        Assert.Equal(new[] { "proje", "proje-kapak.PNG", "proje.ä" }, results.Select(result => result.Item.Name));
        var excluded = filter with
        {
            IncludeDirectories = false,
            AllowedExtensions = new HashSet<string>([".pdf"], StringComparer.OrdinalIgnoreCase)
        };
        Assert.Empty(index.TokenItemsWithPartial("proje", null, excluded, out found, CancellationToken.None));
        Assert.True(found);
    }

    [Fact]
    public void ParsedAiExpandsFolderContentsInOneCatalogPass()
    {
        var entries = new List<IndexEntry> { new(Root, Root, ".", FileAttributes.Directory, 0, 0) };
        for (var i = 0UL; i < 64; i++)
        {
            var folder = Reference(100 + i);
            entries.Add(new(folder, Root, $"proje-{i}", FileAttributes.Directory, 0, 0));
            entries.Add(new(Reference(200 + i), folder, $"kapak-{i}.png", FileAttributes.Archive, 128, 0));
            entries.Add(new(Reference(300 + i), folder, $"not-{i}.txt", FileAttributes.Archive, 128, 0));
        }
        entries.Add(new(Reference(900), Root, "yayin", FileAttributes.Directory, 0, 0));
        entries.Add(new(Reference(901), Reference(900), "tanitim.png", FileAttributes.Archive, 128, 0));
        entries.Add(new(Reference(902), Root, "alakasiz.png", FileAttributes.Archive, 128, 0));
        using var index = FileIndex.Build(@"C:\Kok", Root, entries);
        var catalog = new SeparateCalls(new FileIndexReader([(index, null)]));
        var engine = new AdvancedSearchEngine(_ => catalog, new BasicTokenizer(), new BasicScoringStrategy());

        var results = engine.Search(new StructuredQuery
        {
            OriginalText = "proje resim dosyaları",
            OriginalTerms = ["proje"],
            HardExtensions = [".png"],
            TargetType = new TargetType { File = 1, Folder = 0 },
            FolderHints = [new FolderHint { Name = "yayin", Weight = 1 }]
        });

        Assert.Equal(65, results.Count);
        Assert.Contains(results, item => item.Name == "tanitim.png");
        Assert.All(results, item => Assert.EndsWith(".png", item.Name));
        Assert.DoesNotContain(results, item => item.Name == "alakasiz.png");
        Assert.Equal(1, catalog.CatalogReads);
        Assert.Equal(0, catalog.DescendantReads);
    }

    [Fact]
    public void FilterOnlyListsNestedFoldersWithoutReadingSubtrees()
    {
        var recent = DateTime.UtcNow.AddDays(-10).Ticks;
        var old = DateTime.UtcNow.AddDays(-400).Ticks;
        using var index = FileIndex.Build(@"C:\Kok", Root,
        [
            new IndexEntry(Root, Root, ".", FileAttributes.Directory, 0, old, old),
            new IndexEntry(Reference(30), Root, "Yeni", FileAttributes.Directory, 0, recent, recent),
            new IndexEntry(Reference(31), Reference(30), "Ic", FileAttributes.Directory, 0, recent, recent),
            new IndexEntry(Reference(32), Reference(31), "a.txt", FileAttributes.Archive, 64, recent, recent),
            new IndexEntry(Reference(40), Root, "Eski", FileAttributes.Directory, 0, old, old),
            new IndexEntry(Reference(41), Reference(40), "IcYeni", FileAttributes.Directory, 0, recent, recent),
            new IndexEntry(Reference(42), Reference(40), "b.txt", FileAttributes.Archive, 64, recent, recent),
            new IndexEntry(Reference(43), Reference(40), "c.txt", FileAttributes.Archive, 64, old, old)
        ]);
        var catalog = new SeparateCalls(new FileIndexReader([(index, null)]));
        var engine = new AdvancedSearchEngine(_ => catalog, new BasicTokenizer(), new BasicScoringStrategy());

        var results = engine.Search(new StructuredQuery
        {
            FilterOnlyMode = true,
            IncludeFolderContents = true,
            TargetType = new TargetType { File = 0.6, Folder = 0.4 },
            DateFilter = new DateFilter { ModifiedAfter = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd") }
        });

        Assert.Equal(new[] { "Ic", "a.txt", "b.txt" }, results.Select(result => result.Name).Order(StringComparer.Ordinal));
        Assert.Equal(1, catalog.CatalogReads);
        Assert.Equal(0, catalog.DescendantReads);
    }

    private sealed class SeparateCalls(FileIndexReader inner) : IQueryCatalogSnapshot, ITokenIndexSnapshot
    {
        public int CatalogReads { get; private set; }
        public int DescendantReads { get; private set; }
        public int ItemCount => inner.ItemCount;
        public int TokenCount => inner.TokenCount;
        public bool IsTokenIndexReady => true;
        public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken cancellationToken = default) => inner.Get(token, cancellationToken);
        public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken cancellationToken = default) =>
            inner.GetPartial(token, cancellationToken);
        public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken cancellationToken = default) =>
            inner.GetFuzzy(token, maxDistance, cancellationToken);
        public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken cancellationToken = default) => inner.GetAllItems(cancellationToken);
        public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken cancellationToken = default)
        {
            DescendantReads++;
            return inner.GetDescendants(item, cancellationToken);
        }
        public bool TryGetItem(string path, out SearchItem item) => inner.TryGetItem(path, out item);
        public IReadOnlyCollection<SearchItem> GetRoots(CancellationToken cancellationToken = default) => inner.GetRoots(cancellationToken);
        public IReadOnlyCollection<SearchItem> GetChildren(string path, CancellationToken cancellationToken = default) =>
            inner.GetChildren(path, cancellationToken);
        public IEnumerable<SearchItem> GetItems(QueryCatalogFilter filter, CancellationToken cancellationToken = default)
        {
            CatalogReads++;
            return inner.GetItems(filter, cancellationToken);
        }
        public bool ContainsPath(string path) => inner.ContainsPath(path);
        public ISearchStateReader WithUpserts(IEnumerable<FileSystemNode> nodes, ITokenizer tokenizer) => throw new NotSupportedException();
        public ISearchStateReader WithoutPathAndDescendants(string path) => throw new NotSupportedException();
        public ISearchStateReader WithChanges(IEnumerable<string> removedPaths, IEnumerable<FileSystemNode> upserts, ITokenizer tokenizer) =>
            throw new NotSupportedException();
    }

    [Fact]
    public void FilterOnlyAiSearchFiltersByCreationDate()
    {
        using var index = Build();
        var engine = Engine(index);
        var query = new StructuredQuery
        {
            FilterOnlyMode = true,
            DateFilter = new DateFilter { CreatedAfter = DateTime.Now.AddDays(-30).ToString("yyyy-MM-dd") }
        };

        var names = Names(engine.Search(query, 50));

        Assert.Contains("fatura_mart.pdf", names);
        Assert.Contains("efatura2024.pdf", names);
        Assert.DoesNotContain("notlar.txt", names);
        Assert.DoesNotContain("Işık raporu.docx", names);
    }
}
