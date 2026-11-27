using System.Globalization;
using System.Net;
using System.Text;
using System.Text.Json;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class IntentLocalEvidenceTests
{
    private const string Root = @"C:\Synthetic";
    private const string NoFilters = """
        {"mode":"keyword","target":"file","hard_extensions":[],"soft_extensions":[],"folders":[],"open":false}
        """;

    [Fact]
    public async Task AnInflectedDayKeepsItsDateAndLeavesOnlyTheTopic()
    {
        using var index = Build(("A/rapor.pdf", "2026-11-20", "2026-11-26"), ("B/rapor.pdf", "2026-11-26", "2026-11-26"),
            ("A/dünkü-not.txt", "2026-11-26", "2026-11-26"));

        var outcome = await ExecuteAsync(index, "dünkü rapor", NoFilters, Anchors("rapor"));

        Assert.Equal("2026-11-26", outcome.StructuredQuery!.DateFilter?.CreatedAfter);
        Assert.Equal("2026-11-27", outcome.StructuredQuery.DateFilter?.CreatedBeforeExclusive);
        Assert.Equal(new[] { "rapor" }, outcome.StructuredQuery.OriginalTerms);
        Assert.Equal(@"C:\Synthetic\B\rapor.pdf", Assert.Single(outcome.Results).FullPath);
    }

    [Fact]
    public async Task AnInflectedPeriodKeepsTheModelRange()
    {
        using var index = Build(("A/fatura.pdf", "2026-10-20", "2026-10-20"), ("B/fatura.pdf", "2026-10-03", "2026-10-03"));
        const string intent = """
            {"mode":"keyword","target":"file","folders":[],"created_from":"2026-10-01","created_to_exclusive":"2026-10-08","open":false}
            """;

        var outcome = await ExecuteAsync(index, "geçen ayın başında oluşturduğum fatura", intent, Anchors("fatura"));

        Assert.Equal(new[] { "fatura" }, outcome.StructuredQuery!.OriginalTerms);
        Assert.Equal("2026-10-01", outcome.StructuredQuery.DateFilter?.CreatedAfter);
        Assert.Equal(@"C:\Synthetic\B\fatura.pdf", Assert.Single(outcome.Results).FullPath);
    }

    [Fact]
    public async Task AModelRangeInTheWrongDateFieldMovesToTheFieldTheTextNames()
    {
        using var index = Build(("A/proje-özeti.docx", "2026-11-18", "2026-11-02"),
            ("B/proje-özeti.docx", "2026-10-01", "2026-11-18"));
        const string intent = """
            {"mode":"keyword","target":"file","folders":[],"created_from":"2026-11-16","created_to_exclusive":"2026-11-23","open":false}
            """;

        var outcome = await ExecuteAsync(index, "geçen hafta değiştirdiğim proje özeti", intent, Anchors("proje", "özet"));

        var date = outcome.StructuredQuery!.DateFilter;
        Assert.NotNull(date);
        Assert.Null(date.CreatedAfter);
        Assert.Equal("2026-11-16", date.ModifiedAfter);
        Assert.Equal("2026-11-23", date.ModifiedBeforeExclusive);
        Assert.Equal(@"C:\Synthetic\B\proje-özeti.docx", outcome.Results[0].FullPath);
        Assert.DoesNotContain(outcome.Results, result => result.FullPath.StartsWith(@"C:\Synthetic\A\", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("sunum", "Toplanti/plan.pptx|Egitim/giris.odp")]
    [InlineData("fotoğraflar", "Kare/IMG_01.jpg|Kare/IMG_02.heic")]
    [InlineData("2026 bütçe excel", "Finans/2026-bütçe.xlsx|Finans/2025-bütçe.xlsx")]
    public async Task FormatWordsSelectTheirWholeFileFamily(string query, string expected)
    {
        using var index = Build(("Toplanti/plan.pptx", "2026-11-01", "2026-11-01"), ("Egitim/giris.odp", "2026-11-01", "2026-11-01"),
            ("Not/sunum-notu.txt", "2026-11-01", "2026-11-01"), ("Kare/IMG_01.jpg", "2026-11-01", "2026-11-01"),
            ("Kare/IMG_02.heic", "2026-11-01", "2026-11-01"), ("Not/fotoğraflar.txt", "2026-11-01", "2026-11-01"),
            ("Finans/2026-bütçe.xlsx", "2026-11-01", "2026-11-01"), ("Finans/2025-bütçe.xlsx", "2026-11-01", "2026-11-01"),
            ("Arsiv/2026-bütçe.pdf", "2026-11-01", "2026-11-01"), ("Kurs/excel-2026.txt", "2026-11-01", "2026-11-01"));

        var outcome = await ExecuteAsync(index, query, NoFilters, Anchors(query.Split(' ')));

        Assert.Equal(SearchExecutionMode.Advanced, outcome.Mode);
        Assert.Equal(expected.Split('|').Select(path => Path.Combine(Root, path.Replace('/', '\\'))).Order(StringComparer.Ordinal),
            outcome.Results.Select(result => result.FullPath).Order(StringComparer.Ordinal));
    }

    [Fact]
    public async Task ConversationalFillersAreNotNameEvidence()
    {
        using var index = Build(("Gezi/boarding-pass.pdf", "2026-11-01", "2026-11-01"), ("Not/şey-vardı.txt", "2026-11-01", "2026-11-01"),
            ("Ev/su-faturası.pdf", "2026-11-01", "2026-11-01"));
        const string keywords = """
            {"anchors":[{"primary":"biniş kartı","variants":[],"translations":["boarding pass"]}],"phrases":["biniş kartı"],"context":[]}
            """;

        var filler = await ExecuteAsync(index, "hani şu yolculuk için aldığım şey vardı ya biniş kartını bul", NoFilters, keywords);
        var water = await ExecuteAsync(index, "su faturası", NoFilters, Anchors("fatura"));

        Assert.Empty(filler.StructuredQuery!.OriginalTerms.Intersect(["hani", "su", "sey", "vardi", "ya"]));
        Assert.Equal(@"C:\Synthetic\Gezi\boarding-pass.pdf", filler.Results[0].FullPath);
        Assert.Contains("su", water.StructuredQuery!.OriginalTerms);
    }

    [Fact]
    public async Task AnIdentifierMustAppearWholeWhateverItsSeparator()
    {
        using var index = Build(("Is/ZXQ-1111.txt", "2026-11-01", "2026-11-01"), ("Is/ZXQ-9918.pdf", "2026-11-01", "2026-11-01"),
            ("Is/9917-etiket.md", "2026-11-01", "2026-11-01"), ("Is/ZXQ_9917 son.pdf", "2026-11-01", "2026-11-01"));
        const string split = """{"anchors":[{"primary":"ZXQ"},{"primary":"9917"}],"phrases":[],"context":[]}""";

        var single = await ExecuteAsync(index, "ZXQ-9917 dosyasını bul", NoFilters, split);
        var missing = await ExecuteAsync(index, "olmayan ZXQ-9919 dosyasını bul", NoFilters, split);
        var pair = await ExecuteAsync(index, "ZXQ-1111 ve ZXQ-9918", NoFilters, split);
        var bare = await ExecuteAsync(index, "ZXQ-9917", NoFilters, split);
        var bareMissing = await ExecuteAsync(index, "ZXQ-9919", NoFilters, split);

        Assert.Equal(@"C:\Synthetic\Is\ZXQ_9917 son.pdf", Assert.Single(single.Results).FullPath);
        Assert.Empty(missing.Results);
        Assert.Equal(SearchExecutionMode.Standard, bare.Mode);
        Assert.Equal(@"C:\Synthetic\Is\ZXQ_9917 son.pdf", Assert.Single(bare.Results).FullPath);
        Assert.Empty(bareMissing.Results);
        Assert.Null(pair.StructuredQuery!.RequiredName);
        Assert.Contains(pair.Results, result => result.Name == "ZXQ-1111.txt");
    }

    [Fact]
    public async Task AWordSplitFolderNameOutranksDeepFoldersUnderAnAncestorOfTheSameName()
    {
        using var index = Build(("Profil/lale/paket00/tr/ceviri.json", "2026-11-01", "2026-11-01"),
            ("Profil/lale/paket01/tr/ceviri.json", "2026-11-01", "2026-11-01"),
            ("Projeler/lale.tr/index.html", "2026-11-01", "2026-11-01"));
        const string intent = """{"mode":"keyword","target":"folder","folders":["lale"],"open":false}""";
        const string keywords = """{"anchors":[{"primary":"lale"}],"phrases":[],"context":["tr"]}""";

        var outcome = await ExecuteAsync(index, "lale tr", intent, keywords);

        Assert.Equal(@"C:\Synthetic\Projeler\lale.tr", outcome.Results[0].FullPath);
        Assert.DoesNotContain(outcome.Results, result => result.Name == "tr" && result.Score >= 2000);
    }

    [Fact]
    public async Task AnExplicitAncestorPrefersItsDirectChildFolder()
    {
        using var index = Build(("Ayrık Matematik/Arşiv/Çıkmış Sorular/s1.pdf", "2026-11-01", "2026-11-01"),
            ("Ayrık Matematik/Çıkmış Sorular/s2.pdf", "2026-11-01", "2026-11-01"));
        const string intent = """{"mode":"keyword","target":"folder","folders":[],"open":false}""";

        var outcome = await ExecuteAsync(index, "Ayrık Matematik altındaki Çıkmış Sorular klasörünü", intent,
            Anchors("çıkmış sorular"));

        Assert.Equal(@"C:\Synthetic\Ayrık Matematik\Çıkmış Sorular", outcome.Results[0].FullPath);
        Assert.Equal(2000, outcome.Results[0].Score);
        Assert.Equal(1000, Assert.Single(outcome.Results,
            result => result.FullPath == @"C:\Synthetic\Ayrık Matematik\Arşiv\Çıkmış Sorular").Score);
    }

    private static string Anchors(params string[] words) => JsonSerializer.Serialize(new
    {
        anchors = words.Select(word => new { primary = word, variants = Array.Empty<string>(), translations = Array.Empty<string>() }),
        phrases = Array.Empty<string>(),
        context = Array.Empty<string>()
    });

    private static FileIndex Build(params (string Path, string Created, string Modified)[] files)
    {
        const ulong root = 5;
        var entries = new List<IndexEntry> { new(root, root, ".", FileAttributes.Directory, 0, 0, 0) };
        var folders = new Dictionary<string, ulong>(StringComparer.OrdinalIgnoreCase) { [""] = root };
        ulong next = 16;
        foreach (var (path, created, modified) in files)
        {
            var names = path.Split('/');
            var parent = "";
            foreach (var name in names[..^1])
            {
                var folder = parent.Length == 0 ? name : parent + "/" + name;
                if (!folders.ContainsKey(folder))
                {
                    folders[folder] = next++;
                    entries.Add(new(folders[folder], folders[parent], name, FileAttributes.Directory, 0, 0, 0));
                }
                parent = folder;
            }
            entries.Add(new(next++, folders[parent], names[^1], FileAttributes.Archive, 1024, Ticks(modified), Ticks(created)));
        }
        return FileIndex.Build(Root, root, entries);

        static long Ticks(string day) => DateTime.ParseExact(day, "yyyy-MM-dd", CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal).AddHours(12).Ticks;
    }

    private static async Task<SearchOutcome> ExecuteAsync(FileIndex index, string query, string intent, string keywords)
    {
        using var intentClient = new HttpClient(new CompletionHandler(intent));
        using var keywordClient = new HttpClient(new CompletionHandler(keywords));
        var parser = new IntentParser(intentClient, keywordClient,
            nowProvider: () => new DateTimeOffset(2026, 11, 27, 12, 0, 0, TimeSpan.Zero),
            timeZone: TimeZoneInfo.Utc, model: "qwen/qwen3.8-27b");
        var reader = new FileIndexReader([(index, null)]);
        var tokenizer = new BasicTokenizer();
        var scoring = new BasicScoringStrategy();
        var standard = new SearchEngine(_ => reader, tokenizer, scoring, literalNamesOnly: true);
        var advanced = new AdvancedSearchEngine(_ => reader, tokenizer, scoring);
        var service = new SearchApplicationService(standard.Search, advanced.Search,
            parser.ParseWithGroqAsync, parser.ParseIntent);
        return await service.SearchAsync(new SearchRequest(query, true, true, 100));
    }

    private sealed class CompletionHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            var response = JsonSerializer.Serialize(new
            {
                choices = new[] { new { finish_reason = "stop", message = new { content = payload } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
