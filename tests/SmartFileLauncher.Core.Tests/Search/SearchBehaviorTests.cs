using SmartFileLauncher.Core.DataStructures;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class SearchBehaviorTests
{
    [Fact]
    public void StandardSearchRanksExactFileNameFirst()
    {
        var index = new InvertedIndex();
        var tokenizer = new BasicTokenizer();
        var exact = new FileSystemNode(
            "budget-report.txt",
            @"C:\Workspace\budget-report.txt",
            false);
        var partial = new FileSystemNode(
            "budget-report-final.txt",
            @"C:\Workspace\budget-report-final.txt",
            false);

        AddToIndex(index, tokenizer, exact, partial);
        var engine = new SearchEngine(
            index,
            tokenizer,
            new BasicScoringStrategy());

        var results = engine.Search("budget-report.txt", maxResults: 1);

        var result = Assert.Single(results);
        Assert.Equal(exact.FullPath, result.FullPath);
        Assert.False(result.IsDirectory);
    }

    [Fact]
    public void StandardSearchMarksDirectoryResults()
    {
        var index = new InvertedIndex();
        var tokenizer = new BasicTokenizer();
        var directory = new FileSystemNode(
            "Reports",
            @"C:\Workspace\Reports",
            true);

        AddToIndex(index, tokenizer, directory);
        var engine = new SearchEngine(
            index,
            tokenizer,
            new BasicScoringStrategy());

        var result = Assert.Single(engine.Search("Reports"));

        Assert.True(result.IsDirectory);
    }

    [Fact]
    public void StandardSearchHonorsResultLimit()
    {
        var index = new InvertedIndex();
        var tokenizer = new BasicTokenizer();
        var nodes = Enumerable.Range(1, 5)
            .Select(number => new FileSystemNode(
                $"report-{number}.txt",
                $@"C:\Workspace\report-{number}.txt",
                false))
            .ToArray();

        AddToIndex(index, tokenizer, nodes);
        var engine = new SearchEngine(
            index,
            tokenizer,
            new BasicScoringStrategy());

        var results = engine.Search("report", maxResults: 2);

        Assert.Equal(2, results.Count);
    }

    [Fact]
    public void AdvancedSearchAppliesFolderAndExtensionFiltersTogether()
    {
        var root = new FileSystemNode("User", @"C:\Users\person", true);
        var downloads = new FileSystemNode(
            "Downloads",
            @"C:\Users\person\Downloads",
            true);
        var desktop = new FileSystemNode(
            "Desktop",
            @"C:\Users\person\Desktop",
            true);
        var matchingPdf = new FileSystemNode(
            "invoice.pdf",
            @"C:\Users\person\Downloads\invoice.pdf",
            false);
        var wrongExtension = new FileSystemNode(
            "invoice.txt",
            @"C:\Users\person\Downloads\invoice.txt",
            false);
        var wrongFolder = new FileSystemNode(
            "invoice.pdf",
            @"C:\Users\person\Desktop\invoice.pdf",
            false);

        root.AddChild(downloads);
        root.AddChild(desktop);
        downloads.AddChild(matchingPdf);
        downloads.AddChild(wrongExtension);
        desktop.AddChild(wrongFolder);

        var engine = new AdvancedSearchEngine(
            new InvertedIndex(),
            new BasicTokenizer(),
            new BasicScoringStrategy(),
            root);
        var query = new StructuredQuery
        {
            FilterOnlyMode = true,
            PredictedExtensions = new List<string> { "pdf" },
            FolderHints = new List<FolderHint>
            {
                new() { Name = "downloads", Weight = 1 }
            }
        };

        var results = engine.Search(query);

        var result = Assert.Single(results);
        Assert.Equal(matchingPdf.FullPath, result.FullPath);
    }

    [Fact]
    public void AdvancedSearchMarksDirectoryResults()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var reports = new FileSystemNode(
            "Reports",
            @"C:\Root\Reports",
            true);
        root.AddChild(reports);
        var engine = new AdvancedSearchEngine(
            new InvertedIndex(),
            new BasicTokenizer(),
            new BasicScoringStrategy(),
            root);
        var query = new StructuredQuery
        {
            FilterOnlyMode = true,
            TargetType = new TargetType { File = 0, Folder = 1 }
        };

        var result = Assert.Single(engine.Search(query));

        Assert.Equal(reports.FullPath, result.FullPath);
        Assert.True(result.IsDirectory);
    }

    [Fact]
    public void AdvancedSearchUsesCategoryWeightsWhenRanking()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var intended = new FileSystemNode(
            "2024-budget-report.xlsx",
            @"C:\Root\2024-budget-report.xlsx",
            false);
        var noisy = new FileSystemNode(
            "finance-report-final.xlsx",
            @"C:\Root\finance-report-final.xlsx",
            false);
        root.AddChild(intended);
        root.AddChild(noisy);

        var index = new InvertedIndex();
        var tokenizer = new BasicTokenizer();
        AddToIndex(index, tokenizer, intended, noisy);
        var engine = new AdvancedSearchEngine(
            index,
            tokenizer,
            new BasicScoringStrategy(),
            root);
        var query = new StructuredQuery
        {
            Keywords = ["2024 budget report", "finance", "final"],
            SearchTerms =
            [
                new() { Text = "2024 budget report", Category = SearchTermCategory.Exact, Weight = 1 },
                new() { Text = "finance", Category = SearchTermCategory.Related, Weight = 0.3 },
                new() { Text = "final", Category = SearchTermCategory.Related, Weight = 0.25 }
            ]
        };

        var results = engine.Search(query);

        Assert.Equal(intended.FullPath, results[0].FullPath);
        Assert.True(results[0].Score > results[1].Score);
    }

    [Fact]
    public void AdvancedSearchUsesSoftExtensionsOnlyAsRankingSignal()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var pdf = new FileSystemNode("invoice.pdf", @"C:\Root\invoice.pdf", false);
        var text = new FileSystemNode("invoice.txt", @"C:\Root\invoice.txt", false);
        root.AddChild(text);
        root.AddChild(pdf);

        var index = new InvertedIndex();
        var tokenizer = new BasicTokenizer();
        AddToIndex(index, tokenizer, text, pdf);
        var engine = new AdvancedSearchEngine(
            index,
            tokenizer,
            new BasicScoringStrategy(),
            root);
        var query = new StructuredQuery
        {
            Keywords = ["invoice"],
            SearchTerms =
            [
                new() { Text = "invoice", Category = SearchTermCategory.Exact, Weight = 1 }
            ],
            SoftExtensions = ["pdf"]
        };

        var results = engine.Search(query);

        Assert.Equal(2, results.Count);
        Assert.Equal(pdf.FullPath, results[0].FullPath);
    }

    [Fact]
    public void AdvancedSearchTreatsDateUpperBoundAsExclusive()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var sameDay = new FileSystemNode("today.txt", @"C:\Root\today.txt", false)
        {
            Metadata = new FileMetadata
            {
                CreatedTime = new DateTime(2026, 7, 31, 15, 30, 0)
            }
        };
        var nextDay = new FileSystemNode("tomorrow.txt", @"C:\Root\tomorrow.txt", false)
        {
            Metadata = new FileMetadata
            {
                CreatedTime = new DateTime(2026, 8, 1, 0, 0, 0)
            }
        };
        root.AddChild(sameDay);
        root.AddChild(nextDay);

        var engine = new AdvancedSearchEngine(
            new InvertedIndex(),
            new BasicTokenizer(),
            new BasicScoringStrategy(),
            root);
        var query = new StructuredQuery
        {
            FilterOnlyMode = true,
            HardExtensions = ["txt"],
            DateFilter = new DateFilter
            {
                CreatedAfter = "2026-07-31",
                CreatedBeforeExclusive = "2026-08-01"
            }
        };

        var result = Assert.Single(engine.Search(query));

        Assert.Equal(sameDay.FullPath, result.FullPath);
    }

    [Fact]
    public void AdvancedSearchRequiresTicketAnchorAndUsesSummerOnlyForRanking()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var summerTicket = new FileSystemNode("yaz-bilet.pdf", @"C:\Root\yaz-bilet.pdf", false)
        {
            Metadata = new FileMetadata { CreatedTime = new DateTime(2026, 7, 10) }
        };
        var ticket = new FileSystemNode("bilet.pdf", @"C:\Root\bilet.pdf", false)
        {
            Metadata = new FileMetadata { CreatedTime = new DateTime(2026, 7, 11) }
        };
        var summerNoise = new FileSystemNode("Yaza-merhaba.pdf", @"C:\Root\Yaza-merhaba.pdf", false)
        {
            Metadata = new FileMetadata { CreatedTime = new DateTime(2026, 7, 12) }
        };
        var subtitleNoise = new FileSystemNode("altyazi-belirteci.html", @"C:\Root\altyazi-belirteci.html", false)
        {
            Metadata = new FileMetadata { CreatedTime = new DateTime(2026, 7, 13) }
        };
        var earlyTicket = new FileSystemNode("erken-bilet.pdf", @"C:\Root\erken-bilet.pdf", false)
        {
            Metadata = new FileMetadata { CreatedTime = new DateTime(2026, 5, 31) }
        };
        var engine = CreateAdvancedEngine(
            root,
            summerTicket,
            ticket,
            summerNoise,
            subtitleNoise,
            earlyTicket);
        var query = new StructuredQuery
        {
            SearchTerms =
            [
                new()
                {
                    Text = "bilet",
                    Category = SearchTermCategory.Exact,
                    Role = SearchTermRole.Anchor,
                    AnchorGroup = 0,
                    Weight = 1
                },
                new()
                {
                    Text = "biletler",
                    Category = SearchTermCategory.Variant,
                    Role = SearchTermRole.Anchor,
                    AnchorGroup = 0,
                    Weight = 0.9
                },
                new()
                {
                    Text = "yaz bilet",
                    Category = SearchTermCategory.Exact,
                    Role = SearchTermRole.Phrase,
                    AnchorGroup = -1,
                    Weight = 0.75
                },
                new()
                {
                    Text = "yaz",
                    Category = SearchTermCategory.Related,
                    Role = SearchTermRole.Context,
                    AnchorGroup = -1,
                    Weight = 0.35
                }
            ],
            DateFilter = new DateFilter
            {
                CreatedAfter = "2026-06-01",
                CreatedBeforeExclusive = "2026-09-01"
            }
        };

        var results = engine.Search(query);

        Assert.Equal(2, results.Count);
        Assert.Equal(summerTicket.FullPath, results[0].FullPath);
        Assert.Equal(ticket.FullPath, results[1].FullPath);
        Assert.DoesNotContain(results, result => result.FullPath == summerNoise.FullPath);
        Assert.DoesNotContain(results, result => result.FullPath == subtitleNoise.FullPath);
        Assert.DoesNotContain(results, result => result.FullPath == earlyTicket.FullPath);
    }

    [Fact]
    public void AdvancedSearchTreatsTranslationsAsAlternativesWithinOneAnchor()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var turkish = new FileSystemNode("bilet.pdf", @"C:\Root\bilet.pdf", false);
        var english = new FileSystemNode("ticket.pdf", @"C:\Root\ticket.pdf", false);
        var unrelated = new FileSystemNode("fatura.pdf", @"C:\Root\fatura.pdf", false);
        var engine = CreateAdvancedEngine(root, turkish, english, unrelated);
        var query = new StructuredQuery
        {
            SearchTerms =
            [
                new()
                {
                    Text = "bilet",
                    Category = SearchTermCategory.Exact,
                    Role = SearchTermRole.Anchor,
                    AnchorGroup = 0,
                    Weight = 1
                },
                new()
                {
                    Text = "ticket",
                    Category = SearchTermCategory.Translation,
                    Role = SearchTermRole.Anchor,
                    AnchorGroup = 0,
                    Weight = 0.8
                }
            ]
        };

        var paths = engine.Search(query).Select(result => result.FullPath).ToHashSet();

        Assert.Equal(2, paths.Count);
        Assert.Contains(turkish.FullPath, paths);
        Assert.Contains(english.FullPath, paths);
        Assert.DoesNotContain(unrelated.FullPath, paths);
    }

    [Fact]
    public void AdvancedSearchRequiresEveryExplicitAnchorGroup()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var intended = new FileSystemNode(
            "Ayse-Demir-mezuniyet.jpg",
            @"C:\Root\Ayse-Demir-mezuniyet.jpg",
            false);
        var onlyName = new FileSystemNode("Ayse-Demir.jpg", @"C:\Root\Ayse-Demir.jpg", false);
        var onlyEvent = new FileSystemNode("mezuniyet.jpg", @"C:\Root\mezuniyet.jpg", false);
        var engine = CreateAdvancedEngine(root, intended, onlyName, onlyEvent);
        var query = new StructuredQuery
        {
            SearchTerms =
            [
                new()
                {
                    Text = "Ayse Demir",
                    Category = SearchTermCategory.Exact,
                    Role = SearchTermRole.Anchor,
                    AnchorGroup = 0,
                    Weight = 1
                },
                new()
                {
                    Text = "mezuniyet",
                    Category = SearchTermCategory.Exact,
                    Role = SearchTermRole.Anchor,
                    AnchorGroup = 1,
                    Weight = 1
                }
            ]
        };

        var result = Assert.Single(engine.Search(query));

        Assert.Equal(intended.FullPath, result.FullPath);
    }

    [Fact]
    public void AdvancedSearchRequiresAllTokensOfMultiwordAnchor()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var intended = new FileSystemNode("bütçe-raporu.xlsx", @"C:\Root\bütçe-raporu.xlsx", false);
        var onlyBudget = new FileSystemNode("bütçe.xlsx", @"C:\Root\bütçe.xlsx", false);
        var onlyReport = new FileSystemNode("raporu.xlsx", @"C:\Root\raporu.xlsx", false);
        var engine = CreateAdvancedEngine(root, intended, onlyBudget, onlyReport);
        var query = new StructuredQuery
        {
            SearchTerms =
            [
                new()
                {
                    Text = "bütçe raporu",
                    Category = SearchTermCategory.Exact,
                    Role = SearchTermRole.Anchor,
                    AnchorGroup = 0,
                    Weight = 1
                }
            ]
        };

        var result = Assert.Single(engine.Search(query));

        Assert.Equal(intended.FullPath, result.FullPath);
    }

    [Fact]
    public void AdvancedSearchDoesNotUseSubstringOrFuzzyMatchingForShortAnchor()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var unrelated = new FileSystemNode("archive.pdf", @"C:\Root\archive.pdf", false);
        var engine = CreateAdvancedEngine(root, unrelated);
        var query = new StructuredQuery
        {
            SearchTerms =
            [
                new()
                {
                    Text = "cv",
                    Category = SearchTermCategory.Exact,
                    Role = SearchTermRole.Anchor,
                    AnchorGroup = 0,
                    Weight = 1
                }
            ]
        };

        Assert.Empty(engine.Search(query));
    }

    [Fact]
    public void AdvancedSearchKeepsPartialMatchingForNumericIdentifiers()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var flight = new FileSystemNode("FR612.pdf", @"C:\Root\FR612.pdf", false);
        var engine = CreateAdvancedEngine(root, flight);
        var query = new StructuredQuery
        {
            SearchTerms =
            [
                new()
                {
                    Text = "612",
                    Category = SearchTermCategory.Exact,
                    Role = SearchTermRole.Anchor,
                    AnchorGroup = 0,
                    Weight = 1
                }
            ]
        };

        var result = Assert.Single(engine.Search(query));

        Assert.Equal(flight.FullPath, result.FullPath);
    }

    [Fact]
    public void RuleBasedIntentExtractsDocumentTypeAndPdfExtension()
    {
        var parser = new IntentParser();

        var query = parser.ParseIntent("bütçe pdf dosyasını bul");

        Assert.Contains("document", query.FileTypes);
        Assert.Contains(".pdf", query.PredictedExtensions);
        Assert.Contains(".pdf", query.HardExtensions);
        Assert.Contains("bütçe", query.Keywords);
        Assert.Contains(query.SearchTerms, term => term.Text == "bütçe");
    }

    private static void AddToIndex(
        InvertedIndex index,
        ITokenizer tokenizer,
        params FileSystemNode[] nodes)
    {
        foreach (var node in nodes)
        {
            foreach (var token in tokenizer.Tokenize(node.Name))
            {
                index.Add(token, node);
            }
        }
    }

    private static AdvancedSearchEngine CreateAdvancedEngine(
        FileSystemNode root,
        params FileSystemNode[] nodes)
    {
        var index = new InvertedIndex();
        var tokenizer = new BasicTokenizer();
        foreach (var node in nodes)
        {
            root.AddChild(node);
        }

        AddToIndex(index, tokenizer, nodes);
        return new AdvancedSearchEngine(
            index,
            tokenizer,
            new BasicScoringStrategy(),
            root);
    }
}
