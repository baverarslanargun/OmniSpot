using System.Net;
using System.Text;
using System.Text.Json;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class IntentSearchLayoutContractTests
{
    [Theory]
    [InlineData("baver tr resim dosyaları", false)]
    [InlineData("baver.tr fotoğraflar", false)]
    [InlineData("baver.tr klasöründeki resim adlı JPG dosyaları", true)]
    public async Task ImageCategoryFindsFolderContentsAndExplicitNamesNarrowResults(string query, bool explicitScope)
    {
        const ulong root = 5;
        using var index = FileIndex.Build(@"C:\Synthetic", root,
        [
            new(root, root, ".", FileAttributes.Directory, 0, 0, 0),
            new(16, root, "baver.tr", FileAttributes.Directory, 0, 0, 0),
            new(17, 16, "assets", FileAttributes.Directory, 0, 0, 0),
            new(18, 17, "banner.png", FileAttributes.Archive, 100, 0, 0),
            new(19, 17, "logo.svg", FileAttributes.Archive, 100, 0, 0),
            new(20, 16, "resim-kapak.jpg", FileAttributes.Archive, 100, 0, 0),
            new(21, 16, "resim.txt", FileAttributes.Archive, 100, 0, 0),
            new(22, root, "baver.tr-eski", FileAttributes.Directory, 0, 0, 0),
            new(23, 22, "dışarı.jpg", FileAttributes.Archive, 100, 0, 0)
        ]);
        const string intent = """
            {"mode":"keyword","target":"file","folders":["baver tr"],"hard_extensions":["jpg","jpeg","png","gif"],"open":false}
            """;
        const string keywords = """
            {"anchors":[{"primary":"baver","variants":[],"translations":[]},
                {"primary":"resim","variants":["resimleri"],"translations":["image","photo"]}],
             "phrases":[],"context":["tr"]}
            """;

        var outcome = await ExecuteAsync(new FileIndexReader([(index, null)]), intent, keywords, query: query);

        Assert.False(outcome.StructuredQuery!.FilterOnlyMode);
        if (explicitScope)
        {
            Assert.Equal("resim", outcome.StructuredQuery.RequiredName);
            Assert.Single(outcome.StructuredQuery.RequiredFolders);
            Assert.Equal("resim-kapak.jpg", Assert.Single(outcome.Results).Name);
        }
        else
        {
            Assert.Empty(outcome.StructuredQuery.RequiredFolders);
            Assert.Equal(new[] { "banner.png", "logo.svg", "resim-kapak.jpg" },
                outcome.Results.Take(3).Select(result => result.Name).Order(StringComparer.Ordinal));
            Assert.DoesNotContain(outcome.Results, result => result.Name == "resim.txt");
        }
    }

    [Theory]
    [InlineData("a")]
    [InlineData("ab")]
    [InlineData("Abc")]
    public async Task ShortGeneratedKeywordsMatchTheSameNamesAsLiteralSearch(string keyword)
    {
        const ulong root = 5;
        using var index = FileIndex.Build(@"C:\Synthetic", root,
        [
            new(root, root, ".", FileAttributes.Directory, 0, 0, 0),
            new(16, root, "Abc.pdf", FileAttributes.Archive, 100, 0, 0),
            new(17, root, "Abcd.pdf", FileAttributes.Archive, 100, 0, 0),
            new(18, root, "not.pdf", FileAttributes.Archive, 100, 0, 0)
        ]);
        const string intent = """{"mode":"keyword","target":"file","folders":[],"open":false}""";
        var keywords = JsonSerializer.Serialize(new
        {
            anchors = new[] { new { primary = keyword, variants = Array.Empty<string>(), translations = Array.Empty<string>() } },
            phrases = Array.Empty<string>(),
            context = Array.Empty<string>()
        });

        var state = new FileIndexReader([(index, null)]);
        var expected = new SearchEngine(_ => state, new BasicTokenizer(), new BasicScoringStrategy(), literalNamesOnly: true)
            .Search(keyword).Select(result => result.Name).Order().ToArray();
        var outcome = await ExecuteAsync(state, intent, keywords, query: "uygun dosyayı bul");

        Assert.Equal(SearchExecutionMode.Advanced, outcome.Mode);
        Assert.Contains("Abcd.pdf", expected);
        Assert.Equal(expected, outcome.Results.Select(result => result.Name).Order());
    }

    [Theory]
    [InlineData("bav", "baver.tr", true)]
    [InlineData("BAV", "baver.tr", true)]
    [InlineData("baver", "baver.tr", true)]
    [InlineData("baver.tr", "baver.tr", true)]
    [InlineData("derleme-2026", "OmniSpot-derleme-2026.exe", false)]
    public async Task NameUsesTheNormalIndexLookupInAiMode(string query, string name, bool isDirectory)
    {
        const ulong root = 5;
        using var index = FileIndex.Build(@"C:\Synthetic", root,
        [
            new(root, root, ".", FileAttributes.Directory, 0, 0, 0),
            new(16, root, "baver.tr", FileAttributes.Directory, 0, 0, 0),
            new(17, 16, "index.html", FileAttributes.Archive, 100, 0, 0),
            new(18, root, "bav.txt", FileAttributes.Archive, 100, 0, 0),
            new(19, root, "notlar.txt", FileAttributes.Archive, 100, 0, 0),
            new(20, root, "OmniSpot-derleme-2026.exe", FileAttributes.Archive, 100, 0, 0)
        ]);
        const string intent = """{"mode":"keyword","target":"file","folders":[],"open":false}""";
        const string keywords = """
            {"anchors":[{"primary":"bav","variants":[],"translations":[]}],"phrases":[],"context":[]}
            """;

        var state = new FileIndexReader([(index, null)]);
        var standard = new SearchEngine(_ => state, new BasicTokenizer(), new BasicScoringStrategy(), literalNamesOnly: true);
        var expected = standard.Search(query);
        var outcome = await ExecuteAsync(state, intent, keywords, query: query);

        Assert.Contains(expected, result => result.Name == name && result.IsDirectory == isDirectory);
        Assert.Equal(expected.Select(ResultKey), outcome.Results.Select(ResultKey));
        Assert.True(outcome.IsPageable);
        Assert.Null(outcome.AutoOpenPath);
    }

    [Fact]
    public async Task OriginalNameOutranksModelCandidateDespiteUnrequestedMetadata()
    {
        using var workspace = new TemporaryDirectory();
        var original = FileNode("nehir-planı.xlsx", folder: "Projects");
        var modelMatch = FileNode("nakit.pdf", folder: "Downloads");
        const string intent = """
            {"mode":"filter","target":"folder","hard_extensions":["pdf"],"folders":["Downloads"],
             "created_from":"2026-08-01","min_mb":50,"open":true}
            """;
        const string keywords = """
            {"anchors":[{"primary":"nakit","variants":[],"translations":[]}],"phrases":[],"context":[]}
            """;

        var outcome = await ExecuteAsync(CreateState("legacy", [original, modelMatch], workspace), intent, keywords,
            query: "nehir planı dosyası");

        Assert.Equal(SearchExecutionMode.Advanced, outcome.Mode);
        Assert.Equal(new[] { original.FullPath, modelMatch.FullPath }, outcome.Results.Select(result => result.FullPath));
        Assert.Empty(outcome.StructuredQuery!.HardExtensions);
        Assert.Empty(outcome.StructuredQuery.RequiredFolders);
        Assert.Null(outcome.StructuredQuery.DateFilter);
        Assert.Null(outcome.StructuredQuery.SizeFilter);
        Assert.Null(outcome.AutoOpenPath);
    }

    [Fact]
    public async Task ExplicitDateAndFolderWorkWhenTheModelOmitsThem()
    {
        const ulong root = 5;
        var date = new DateTime(2026, 7, 1, 12, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks;
        var previousDay = new DateTime(2026, 6, 30, 12, 0, 0, DateTimeKind.Local).ToUniversalTime().Ticks;
        using var index = FileIndex.Build(@"C:\Synthetic", root,
        [
            new(root, root, ".", FileAttributes.Directory, 0, 0, 0),
            new(16, root, "Arşiv", FileAttributes.Directory, 0, 0, 0),
            new(17, 16, "rapor.pdf", FileAttributes.Archive, 100, date, previousDay),
            new(18, 16, "rapor-eski.pdf", FileAttributes.Archive, 100, previousDay, date),
            new(19, 16, "rapor-klasör", FileAttributes.Directory, 0, previousDay, date),
            new(20, root, "rapor.pdf", FileAttributes.Archive, 100, date, previousDay)
        ]);
        const string intent = """{"mode":"keyword","target":"folder","folders":[],"open":false}""";
        const string keywords = """{"anchors":[{"primary":"rapor","variants":[],"translations":[]}],"phrases":[],"context":[]}""";

        var outcome = await ExecuteAsync(new FileIndexReader([(index, null)]), intent, keywords,
            query: "01.07.2026 tarihinde değiştirilen raporu Arşiv klasöründe ara");

        Assert.Equal(@"C:\Synthetic\Arşiv\rapor.pdf", Assert.Single(outcome.Results).FullPath);
        Assert.Equal("2026-07-01", outcome.StructuredQuery!.DateFilter?.ModifiedAfter);
        Assert.Equal("2026-07-02", outcome.StructuredQuery.DateFilter?.ModifiedBeforeExclusive);
    }

    [Fact]
    public async Task AShortExplicitExtensionStillUsesAiFilters()
    {
        const ulong root = 5;
        using var index = FileIndex.Build(@"C:\Synthetic", root,
        [
            new(root, root, ".", FileAttributes.Directory, 0, 0, 0),
            new(16, root, "pdf", FileAttributes.Directory, 0, 0, 0),
            new(17, root, "kitap.pdf", FileAttributes.Archive, 100, 0, 0),
            new(18, root, "pdf-notlari.txt", FileAttributes.Archive, 100, 0, 0)
        ]);
        const string intent = """{"mode":"filter","target":"file","hard_extensions":["pdf"],"open":false}""";
        const string keywords = """{"anchors":[],"phrases":[],"context":[]}""";

        var outcome = await ExecuteAsync(new FileIndexReader([(index, null)]), intent, keywords, query: "pdf");

        Assert.Equal("kitap.pdf", Assert.Single(outcome.Results).Name);
        Assert.Equal(SearchExecutionMode.Advanced, outcome.Mode);
    }

    [Theory]
    [InlineData("çıkmış sorular ayrık matematik")]
    [InlineData("çıkmış sorular ayrık matematik klasöründe")]
    [InlineData("Ayrık Matematik Çıkmış Sorular")]
    [InlineData("Ayrık Matematik altındaki Çıkmış Sorular klasörünü")]
    [InlineData("cikmis sorular ayrik matematik klasorunde")]
    public async Task ExactFolderAndAncestorNamesSurviveIncorrectAiTerms(string query)
    {
        const ulong root = 5;
        using var index = FileIndex.Build(@"C:\Synthetic", root,
        [
            new(root, root, ".", FileAttributes.Directory, 0, 0, 0),
            new(16, root, "Ayrık Matematik", FileAttributes.Directory, 0, 0, 0),
            new(17, 16, "Çıkmış Sorular", FileAttributes.Directory, 0, 0, 0),
            new(18, 17, "2025.pdf", FileAttributes.Archive, 1024, 0, 0),
            new(20, root, "Matematik", FileAttributes.Directory, 0, 0, 0),
            new(21, 20, "Çıkmış Sorular", FileAttributes.Directory, 0, 0, 0),
            new(22, 16, "Örnek Çıkmış Sorular", FileAttributes.Directory, 0, 0, 0),
            new(30, root, "Ayrık Matematik Çıkmış Sorular.pdf", FileAttributes.Archive, 1024, 0, 0)
        ]);
        const string intent = """{"mode":"keyword","target":"file","folders":["matematik klas"],"open":false}""";
        const string keywords = """
            {"anchors":[{"primary":"çıkış","variants":["cikis"],"translations":["exit"]},
                {"primary":"soru","variants":["sorusu"],"translations":["question"]},
                {"primary":"ayrık","variants":["ayrik"],"translations":["discrete"]}],
             "phrases":["çıkış soruları","ayrık matematik"],"context":["matematik"]}
            """;

        var outcome = await ExecuteAsync(new FileIndexReader([(index, null)]), intent, keywords, query: query);

        var result = outcome.Results[0];
        Assert.Equal(@"C:\Synthetic\Ayrık Matematik\Çıkmış Sorular", result.FullPath);
        Assert.True(result.IsDirectory);
        Assert.Contains(outcome.Results, candidate => candidate.FullPath == @"C:\Synthetic\Matematik\Çıkmış Sorular");
        Assert.Null(outcome.AutoOpenPath);
    }

    [Theory]
    [InlineData("compact32")]
    [InlineData("compact-varint")]
    [InlineData("mapped32")]
    [InlineData("mapped-varint")]
    public void MissingParentDeletionMatchesLegacyForAlternateSeparator(string layout)
    {
        using var workspace = new TemporaryDirectory();
        var parent = new FileSystemNode("missing", @"C:\Synthetic\missing", true);
        var child = new FileSystemNode("orphan.pdf", @"C:\Synthetic\missing/orphan.pdf", false);
        var canonicalChild = new FileSystemNode("canonical.pdf", @"C:\Synthetic\missing\canonical.pdf", false);
        var neighbor = new FileSystemNode("neighbor.pdf", @"C:\Synthetic\missing-neighbor/neighbor.pdf", false);
        parent.AddChild(child);
        parent.AddChild(canonicalChild);
        var nodes = new[] { child, canonicalChild, neighbor };
        var tokenizer = new BasicTokenizer();
        var legacy = SearchState.Create(nodes, tokenizer);
        var compact = CreateState(layout, nodes, workspace);

        var expected = legacy.WithoutPathAndDescendants(parent.FullPath).GetAllItems();
        var updated = compact.WithoutPathAndDescendants(@"c:\SYNTHETIC\missing/");
        Assert.Equal(neighbor.FullPath, Assert.Single(expected).FullPath);
        Assert.Equal(expected, updated.GetAllItems());
        Assert.Empty(updated.Get("orphan"));
        Assert.Empty(updated.Get("canonical"));
        Assert.Single(updated.Get("neighbor"));
        Assert.Equal(3, compact.ItemCount);
    }

    [Theory]
    [InlineData(@"C:\", @"C:\orphan.pdf")]
    [InlineData("C:/", "C:/orphan.pdf")]
    public void MissingParentDeletionAcceptsEitherDriveRootSeparator(string rootPath, string childPath)
    {
        var parent = new FileSystemNode("root", rootPath, true);
        var child = new FileSystemNode("orphan.pdf", childPath, false);
        parent.AddChild(child);
        var tokenizer = new BasicTokenizer();
        var legacy = SearchState.Create([child], tokenizer);
        var compact = CompactSearchState.Create([child], tokenizer);

        Assert.Empty(legacy.WithoutPathAndDescendants(parent.FullPath).GetAllItems());
        Assert.Empty(compact.WithoutPathAndDescendants(parent.FullPath).GetAllItems());
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("compact32")]
    [InlineData("compact-varint")]
    [InlineData("mapped32")]
    [InlineData("mapped-varint")]
    public async Task ParsedIntentKeepsAnchorAlternativesMetadataFiltersAndOpeningDecision(string layout)
    {
        using var workspace = new TemporaryDirectory();
        var nodes = new[]
        {
            FileNode("budget-2026.pdf"),
            FileNode("bütçe-2025.pdf"),
            FileNode("budget-2026.txt"),
            FileNode("bütçe-2026-old.pdf", created: new DateTime(2026, 6, 30)),
            FileNode("bütçe-2026-exclusive.pdf", created: new DateTime(2026, 8, 1)),
            FileNode("bütçe-2026-large.pdf", size: 4 * 1024 * 1024),
            FileNode("bütçe-2026-unknown.pdf", unknown: true),
            FileNode("2026-finance.pdf")
        };
        const string intent = """
            { "mode":"keyword", "target":"file", "hard_extensions":["pdf"],
              "soft_extensions":[], "folders":[], "created_from":"2026-07-01",
              "created_to_exclusive":"2026-08-01", "min_mb":1, "max_mb":3, "open":true }
            """;
        const string keywords = """
            { "anchors":[
                {"primary":"bütçe", "variants":["butce"], "translations":["budget"]},
                {"primary":"2026", "variants":[], "translations":[]}],
              "phrases":[], "context":["finance"] }
            """;
        const string query = "2026 bütçe raporu temmuz ayında oluşturulan 1-3 MB PDF";
        var expected = await ExecuteAsync(CreateState("legacy", nodes, workspace), intent, keywords, query: query);
        var actual = await ExecuteAsync(CreateState(layout, nodes, workspace), intent, keywords, query: query);

        Assert.Equal(new[] { "2026-finance.pdf", "budget-2026.pdf", "bütçe-2025.pdf" },
            actual.Results.Select(result => result.Name).Order(StringComparer.Ordinal));
        Assert.Equal(SearchExecutionMode.Advanced, actual.Mode);
        Assert.False(actual.UsedFallback);
        Assert.Null(actual.WarningMessage);
        Assert.Null(actual.AutoOpenPath);
        Assert.False(actual.StructuredQuery!.OpenAction!.ShouldOpen);
        Assert.Contains(actual.StructuredQuery!.SearchTerms, term =>
            term.Text == "budget" && term.Role == SearchTermRole.Anchor && term.AnchorGroup == 0);
        Assert.Contains(actual.StructuredQuery.SearchTerms, term =>
            term.Text == "2026" && term.Role == SearchTermRole.Anchor && term.AnchorGroup == 1);
        Assert.Contains(actual.StructuredQuery.SearchTerms, term =>
            term.Text == "finance" && term.Role == SearchTermRole.Context);
        AssertSameOutcome(expected, actual);

        const string openQuery = "2026 budget temmuz ayında oluşturulan 1-3 MB PDF aç";
        var explicitOpen = await ExecuteAsync(CreateState(layout, [nodes[0]], workspace), intent, keywords,
            query: openQuery);
        Assert.True(explicitOpen.StructuredQuery!.OpenAction!.ShouldOpen);
        Assert.Equal(@"C:\Synthetic\budget-2026.pdf", explicitOpen.AutoOpenPath);
        var partialOnly = await ExecuteAsync(CreateState(layout, [nodes[^1]], workspace), intent, keywords,
            query: openQuery);
        Assert.Equal("2026-finance.pdf", Assert.Single(partialOnly.Results).Name);
        Assert.Null(partialOnly.AutoOpenPath);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("compact32")]
    [InlineData("compact-varint")]
    [InlineData("mapped32")]
    [InlineData("mapped-varint")]
    public async Task ParsedFilterOnlyIntentFiltersTheWholeCatalogBeforeLimitingResults(string layout)
    {
        using var workspace = new TemporaryDirectory();
        var nodes = Enumerable.Range(0, 150)
            .Select(index => FileNode($"a-{index:D3}.pdf", size: 0, folder: "Downloads"))
            .Append(FileNode("unknown.pdf", unknown: true, folder: "Downloads"))
            .Append(FileNode("a-wrong-folder.pdf", folder: "Documents"))
            .Append(FileNode("a-prefix-neighbor.pdf", folder: "Downloads-old"))
            .Append(FileNode("z-match.pdf", folder: "Downloads"))
            .ToArray();
        const string intent = """
            { "mode":"filter", "target":"file", "hard_extensions":["pdf"],
              "soft_extensions":[], "folders":["Downloads"], "created_from":"2026-07-01",
              "created_to_exclusive":"2026-08-01", "min_mb":1, "max_mb":3, "open":false }
            """;
        const string keywords = """
            { "anchors":[], "phrases":[], "context":[] }
            """;
        const string query = "Downloads klasöründe 2026-07-01 tarihli 1-3 MB PDF";
        var expected = await ExecuteAsync(CreateState("legacy", nodes, workspace), intent, keywords, 1, query);
        var actual = await ExecuteAsync(CreateState(layout, nodes, workspace), intent, keywords, 1, query);

        Assert.Equal(@"C:\Synthetic\Downloads\z-match.pdf", Assert.Single(actual.Results).FullPath);
        Assert.True(actual.StructuredQuery!.FilterOnlyMode);
        Assert.Empty(actual.StructuredQuery.SearchTerms);
        Assert.Null(actual.AutoOpenPath);
        Assert.False(actual.UsedFallback);
        AssertSameOutcome(expected, actual);
    }

    [Theory]
    [InlineData("compact32")]
    [InlineData("compact-varint")]
    [InlineData("mapped32")]
    [InlineData("mapped-varint")]
    public void FilterOnlyFastPathMatchesLegacyForExpansionScoringAndBoundedOrdering(string layout)
    {
        using var workspace = new TemporaryDirectory();
        var downloads = new FileSystemNode("Downloads", @"C:\Synthetic\Downloads", true)
        {
            Metadata = new FileMetadata
            {
                CreatedTime = new DateTime(2026, 7, 10),
                LastWriteTime = new DateTime(2026, 7, 20)
            }
        };
        var nodes = new[]
        {
            downloads,
            FileNode("report-z.pdf", folder: "Downloads"),
            FileNode("report-a.pdf", folder: "Downloads"),
            FileNode("report-b.txt", folder: "Downloads"),
            FileNode("report-old.pdf", created: new DateTime(2026, 6, 30), folder: "Downloads"),
            FileNode("report-unknown.pdf", unknown: true, folder: "Downloads"),
            FileNode("report-c.pdf", folder: "Downloads"),
            FileNode("report-outside.pdf", folder: "Documents")
        };
        foreach (var child in nodes.Skip(1).Take(6)) downloads.AddChild(child);
        nodes[1].Metadata!.OpenCount = 1;
        nodes[2].Metadata!.OpenCount = 4;
        nodes[6].Metadata!.OpenCount = 1;

        var query = new StructuredQuery
        {
            FilterOnlyMode = true,
            IncludeFolderContents = true,
            HardExtensions = ["pdf"],
            SoftExtensions = ["pdf", "txt"],
            FolderHints = [new FolderHint { Name = "Downloads", Weight = 0.8 }],
            TargetType = new TargetType { File = 0.6, Folder = 0.4 },
            DateFilter = new DateFilter
            {
                CreatedAfter = "2026-07-01",
                CreatedBeforeExclusive = "2026-08-01"
            },
            SizeFilter = new SizeFilter { MinMb = 1, MaxMb = 3 },
            SearchTerms =
            [
                new SearchTerm
                {
                    Text = "report-a",
                    Role = SearchTermRole.Phrase,
                    Category = SearchTermCategory.Exact,
                    Weight = 0.9
                },
                new SearchTerm
                {
                    Text = "report",
                    Role = SearchTermRole.Context,
                    Category = SearchTermCategory.Related,
                    Weight = 0.5
                }
            ]
        };
        var tokenizer = new BasicTokenizer();
        var scoring = new BasicScoringStrategy();
        var expected = new AdvancedSearchEngine(
                _ => CreateState("legacy", nodes, workspace), tokenizer, scoring)
            .Search(query, maxResults: 2);
        var actual = new AdvancedSearchEngine(
                _ => CreateState(layout, nodes, workspace), tokenizer, scoring)
            .Search(query, maxResults: 2);

        Assert.Equal(expected.Select(ResultKey), actual.Select(ResultKey));
        Assert.Equal(2, actual.Count);
        Assert.Equal(@"C:\Synthetic\Downloads\report-a.pdf", actual[0].FullPath);
        Assert.Equal(@"C:\Synthetic\Downloads\report-c.pdf", actual[1].FullPath);
    }

    [Theory]
    [InlineData("legacy")]
    [InlineData("compact32")]
    [InlineData("compact-varint")]
    [InlineData("mapped32")]
    [InlineData("mapped-varint")]
    public void MetadataKeepsTimestampKindAndPrecisionAndDistinguishesUnknownFromZero(string layout)
    {
        using var workspace = new TemporaryDirectory();
        var nodes = new[] { DateTimeKind.Unspecified, DateTimeKind.Utc, DateTimeKind.Local }
            .Select(kind => new FileSystemNode(kind + ".pdf", @"C:\Synthetic\" + kind + ".pdf", false)
            {
                Metadata = new FileMetadata
                {
                    SizeBytes = long.MaxValue,
                    CreatedTime = new DateTime(638000000000000001L, kind),
                    LastWriteTime = new DateTime(638000000000000002L, kind),
                    OpenCount = int.MaxValue
                }
            })
            .Append(FileNode("unknown.pdf", unknown: true))
            .Append(FileNode("zero.pdf", size: 0, created: DateTime.MinValue))
            .ToArray();
        var state = CreateState(layout, nodes, workspace);
        foreach (var node in nodes)
        {
            var item = Assert.Single(state.GetAllItems(), item => item.FullPath == node.FullPath);
            var metadata = node.Metadata!;
            Assert.Equal(metadata.SizeBytes, item.SizeBytes);
            Assert.Equal(metadata.CreatedTime?.Ticks, item.CreatedTime?.Ticks);
            Assert.Equal(metadata.CreatedTime?.Kind, item.CreatedTime?.Kind);
            Assert.Equal(metadata.LastWriteTime?.Ticks, item.LastWriteTime?.Ticks);
            Assert.Equal(metadata.LastWriteTime?.Kind, item.LastWriteTime?.Kind);
            Assert.Equal(metadata.OpenCount, item.OpenCount);
        }
    }

    private static FileSystemNode FileNode(string name, long size = 2 * 1024 * 1024,
        DateTime? created = null, bool unknown = false, string? folder = null) =>
        new(name, @"C:\Synthetic\" + (folder == null ? "" : folder + "\\") + name, false)
        {
            Metadata = unknown ? new FileMetadata() : new FileMetadata
            {
                SizeBytes = size,
                CreatedTime = created ?? new DateTime(2026, 7, 1),
                LastWriteTime = new DateTime(2026, 7, 20),
                OpenCount = 3
            }
        };

    private static ISearchStateReader CreateState(string layout, FileSystemNode[] nodes,
        TemporaryDirectory workspace)
    {
        var tokenizer = new BasicTokenizer();
        if (layout == "legacy") return SearchState.Create(nodes, tokenizer);
        var compact = CompactSearchState.Create(nodes, tokenizer,
            varint: layout.EndsWith("varint", StringComparison.Ordinal));
        if (!layout.StartsWith("mapped", StringComparison.Ordinal)) return compact;
        var path = Path.Combine(workspace.Path, Guid.NewGuid() + ".catalog");
        compact.WriteNewBase(path);
        return CompactSearchState.OpenMapped(path);
    }

    private static async Task<SearchOutcome> ExecuteAsync(ISearchStateReader state,
        string intent, string keywords, int limit = 100, string query = "2026 bütçe raporu pdf")
    {
        using var intentClient = new HttpClient(new CompletionHandler(intent));
        using var keywordClient = new HttpClient(new CompletionHandler(keywords));
        var parser = new IntentParser(intentClient, keywordClient,
            nowProvider: () => new DateTimeOffset(2026, 7, 31, 12, 0, 0, TimeSpan.Zero),
            timeZone: TimeZoneInfo.Utc, model: "qwen/qwen3.8-27b");
        var tokenizer = new BasicTokenizer();
        var scoring = new BasicScoringStrategy();
        var standard = new SearchEngine(_ => state, tokenizer, scoring, literalNamesOnly: state is ILiteralNameQuerySnapshot);
        var advanced = new AdvancedSearchEngine(_ => state, tokenizer, scoring);
        var service = new SearchApplicationService(standard.Search, advanced.Search,
            parser.ParseWithGroqAsync, parser.ParseIntent);
        return await service.SearchAsync(new SearchRequest(query, true, true, limit));
    }

    private static void AssertSameOutcome(SearchOutcome expected, SearchOutcome actual)
    {
        Assert.Equal(expected.Results.Select(ResultKey), actual.Results.Select(ResultKey));
        Assert.Equal(expected.Mode, actual.Mode);
        Assert.Equal(expected.UsedFallback, actual.UsedFallback);
        Assert.Equal(expected.FallbackReason, actual.FallbackReason);
        Assert.Equal(expected.WarningMessage, actual.WarningMessage);
        Assert.Equal(expected.AutoOpenPath, actual.AutoOpenPath);
    }

    private static (string, string, bool, double) ResultKey(SearchResult result) =>
        (result.Name, result.FullPath, result.IsDirectory, result.Score);

    private sealed class CompletionHandler(string payload) : HttpMessageHandler
    {
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var response = JsonSerializer.Serialize(new
            {
                choices = new[] { new { message = new { content = payload } } }
            });
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK)
            {
                Content = new StringContent(response, Encoding.UTF8, "application/json")
            });
        }
    }
}
