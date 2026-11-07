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
