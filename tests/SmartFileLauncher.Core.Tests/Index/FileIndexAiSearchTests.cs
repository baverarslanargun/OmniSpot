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

    private sealed class SeparateCalls(FileIndexReader inner) : ISearchStateReader, ITokenIndexSnapshot
    {
        public int ItemCount => inner.ItemCount;
        public int TokenCount => inner.TokenCount;
        public bool IsTokenIndexReady => true;
        public IReadOnlyCollection<SearchItem> Get(string token, CancellationToken cancellationToken = default) => inner.Get(token, cancellationToken);
        public IReadOnlyCollection<SearchItem> GetPartial(string token, CancellationToken cancellationToken = default) =>
            inner.GetPartial(token, cancellationToken);
        public IReadOnlyCollection<SearchItem> GetFuzzy(string token, int maxDistance = 2, CancellationToken cancellationToken = default) =>
            inner.GetFuzzy(token, maxDistance, cancellationToken);
        public IReadOnlyCollection<SearchItem> GetAllItems(CancellationToken cancellationToken = default) => inner.GetAllItems(cancellationToken);
        public IReadOnlyCollection<SearchItem> GetDescendants(SearchItem item, CancellationToken cancellationToken = default) =>
            inner.GetDescendants(item, cancellationToken);
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
