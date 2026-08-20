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
    public void StandardSearchUsesImmutableSearchState()
    {
        var tokenizer = new BasicTokenizer();
        var exact = new FileSystemNode(
            "budget-report.txt",
            @"C:\Workspace\budget-report.txt",
            false)
        {
            Metadata = new FileMetadata { OpenCount = 3 }
        };
        var partial = new FileSystemNode(
            "budget-report-final.txt",
            @"C:\Workspace\budget-report-final.txt",
            false);
        var state = SearchState.Create([exact, partial], tokenizer);
        var providerCalls = 0;
        var engine = new SearchEngine(
            _ =>
            {
                providerCalls++;
                return state;
            },
            tokenizer,
            new BasicScoringStrategy());

        exact.Metadata!.OpenCount = 500;
        var result = Assert.Single(engine.Search("budget-report.txt", maxResults: 1));

        Assert.Equal(1, providerCalls);
        Assert.Equal(exact.FullPath, result.FullPath);
        Assert.Equal(531, result.Score);
    }
    [Fact]
    public void AdvancedSearchUsesOneImmutableStateForFolderExpansion()
    {
        var tokenizer = new BasicTokenizer();
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var archive = new FileSystemNode("Archive", @"C:\Root\Archive", true);
        var report = new FileSystemNode("report.pdf", @"C:\Root\Archive\report.pdf", false)
        {
            Metadata = new FileMetadata { OpenCount = 4 }
        };
        root.AddChild(archive);
        archive.AddChild(report);

        var state = SearchState.Create([archive, report], tokenizer);
        var providerCalls = 0;
        var engine = new AdvancedSearchEngine(
            _ =>
            {
                providerCalls++;
                return state;
            },
            tokenizer,
            new BasicScoringStrategy());

        report.Metadata!.OpenCount = 500;
        var query = new StructuredQuery
        {
            Keywords = ["archive"],
            IncludeFolderContents = true,
            PredictedExtensions = ["pdf"]
        };

        var result = Assert.Single(engine.Search(query));

        Assert.Equal(1, providerCalls);
        Assert.Equal(report.FullPath, result.FullPath);
        Assert.Equal(168, result.Score);
    }

    [Fact]
    public void AdvancedSearchUsesImmutableStateForPartialAndFuzzyMatches()
    {
        var tokenizer = new BasicTokenizer();
        var part = new FileSystemNode("FR612-report.txt", @"C:\Root\FR612-report.txt", false);
        var fuzzy = new FileSystemNode("budget.txt", @"C:\Root\budget.txt", false);
        var state = SearchState.Create([part, fuzzy], tokenizer);
        var engine = new AdvancedSearchEngine(
            _ => state,
            tokenizer,
            new BasicScoringStrategy());

        var partialResult = Assert.Single(engine.Search(new StructuredQuery
        {
            Keywords = ["612"]
        }));
        var fuzzyResult = Assert.Single(engine.Search(new StructuredQuery
        {
            Keywords = ["budjet"]
        }));

        Assert.Equal(part.FullPath, partialResult.FullPath);
        Assert.Equal(fuzzy.FullPath, fuzzyResult.FullPath);
    }

    [Fact]
    public void AdvancedSearchLegacyConstructorPreservesCustomIndexTokens()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var report = new FileSystemNode("report.txt", @"C:\Root\report.txt", false);
        root.AddChild(report);
        var index = new InvertedIndex();
        index.Add("special-token", report);
        var engine = new AdvancedSearchEngine(
            index,
            new BasicTokenizer(),
            new BasicScoringStrategy(),
            root);

        var result = Assert.Single(engine.Search(new StructuredQuery
        {
            Keywords = ["special-token"]
        }));

        Assert.Equal(report.FullPath, result.FullPath);
    }

    [Fact]
    public void SearchStateRemovesDescendantsWhenDirectoryBecomesFile()
    {
        var tokenizer = new BasicTokenizer();
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var directory = new FileSystemNode("report", @"C:\Root\report", true);
        var child = new FileSystemNode("old.txt", @"C:\Root\report\old.txt", false);
        root.AddChild(directory);
        directory.AddChild(child);
        var replacement = new FileSystemNode("report", @"C:\Root\report", false);
        root.AddChild(replacement);

        var state = SearchState.Create([directory, child], tokenizer)
            .WithUpserts([replacement], tokenizer);

        Assert.Empty(state.Get("old"));
        Assert.Empty(state.GetDescendants(Assert.Single(state.Get("report"))));
    }

    [Fact]
    public void AdvancedSearchStopsWhenCancellationOccursBeforeFilterOnlyEnumeration()
    {
        var tokenizer = new BasicTokenizer();
        var node = new FileSystemNode("report.txt", @"C:\Root\report.txt", false);
        var state = SearchState.Create([node], tokenizer);
        using var cancellation = new CancellationTokenSource();
        var engine = new AdvancedSearchEngine(
            _ =>
            {
                cancellation.Cancel();
                return state;
            },
            tokenizer,
            new BasicScoringStrategy());

        Assert.Throws<OperationCanceledException>(() => engine.Search(
            new StructuredQuery { FilterOnlyMode = true },
            cancellationToken: cancellation.Token));
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
        Assert.True(results[0].Score > results[1].Score);
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

    [Fact]
    public void AdvancedSearchAppliesExactFolderHintAndContextSignalsTogether()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var hintedExact = new FileSystemNode(
            "bilet.pdf",
            @"C:\Root\downloads\bilet.pdf",
            false);
        var plainExact = new FileSystemNode(
            "bilet.pdf",
            @"C:\Root\arsiv\bilet.pdf",
            false);
        var contextPartial = new FileSystemNode(
            "bilet-yaz.pdf",
            @"C:\Root\arsiv\bilet-yaz.pdf",
            false);
        var plainPartial = new FileSystemNode(
            "bilet-kis.pdf",
            @"C:\Root\arsiv\bilet-kis.pdf",
            false);
        var engine = CreateAdvancedEngine(
            root,
            hintedExact,
            plainExact,
            contextPartial,
            plainPartial);
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
                    Text = "yaz",
                    Category = SearchTermCategory.Related,
                    Role = SearchTermRole.Context,
                    AnchorGroup = -1,
                    Weight = 0.4
                }
            ],
            FolderHints = new List<FolderHint>
            {
                new() { Name = "Downloads", Weight = 1.4 }
            }
        };

        var results = engine.Search(query);

        double ScoreOf(FileSystemNode node) =>
            results.Single(result => result.FullPath == node.FullPath).Score;

        Assert.Equal(100, ScoreOf(hintedExact) - ScoreOf(plainExact), 6);
        Assert.Equal(75, ScoreOf(plainExact) - ScoreOf(plainPartial), 6);
        Assert.Equal(12, ScoreOf(contextPartial) - ScoreOf(plainPartial), 6);
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
