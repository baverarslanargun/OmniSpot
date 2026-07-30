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

        Assert.NotEmpty(results);
        Assert.All(results, result => Assert.Equal(matchingPdf.FullPath, result.FullPath));
    }

    [Fact]
    public void RuleBasedIntentExtractsDocumentTypeAndPdfExtension()
    {
        var parser = new IntentParser();

        var query = parser.ParseIntent("bütçe pdf dosyasını bul");

        Assert.Contains("document", query.FileTypes);
        Assert.Contains(".pdf", query.PredictedExtensions);
        Assert.Contains("bütçe", query.Keywords);
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
}
