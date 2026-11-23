using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class FileIndexTokenSummaryTests
{
    private const ulong Root = 5;
    private static readonly string[] Tokens = ["report", "REPORT", "notes", "annual", "missing", ""];

    [Fact]
    public void SummaryMatchesTheItemListInEveryScopeAndSampleSize()
    {
        using var index = Left();
        var visible = new[] { @"C:\Visible" };
        foreach (var token in Tokens)
        foreach (var scope in new IReadOnlyList<string>?[] { null, visible })
        foreach (var take in new[] { 0, 1, 3, 10 })
        {
            var items = index.TokenItems(token, TokenMatch.Exact, 0, scope, CancellationToken.None);
            var summary = index.SummarizeToken(token, take, scope, CancellationToken.None);
            Assert.Equal(items.Count, summary.Count);
            Assert.Equal(items.Take(take).Select(item => item.Name), summary.SampleNames);
        }

        Assert.True(index.SummarizeToken("report", 3, visible, CancellationToken.None).Count <
                    index.SummarizeToken("report", 3, null, CancellationToken.None).Count);
    }

    [Fact]
    public void SummaryAcrossVolumesAddsCountsAndFillsSamplesInVolumeOrder()
    {
        using var left = Left();
        using var right = Right();
        using var reader = new FileIndexReader([(right, null), (left, [@"C:\Visible"])]);
        foreach (var token in Tokens)
        foreach (var take in new[] { 0, 1, 3, 10 })
        {
            var items = reader.Get(token);
            var summary = reader.SummarizeToken(token, take);
            Assert.Equal(items.Count, summary.Count);
            Assert.Equal(items.Take(take).Select(item => item.Name), summary.SampleNames);
        }
    }

    [Fact]
    public void DiagnosticsReportTheSameCountsAndSamplesAsTheItemList()
    {
        using var left = Left();
        using var right = Right();
        var reader = new FileIndexReader([(right, null), (left, null)]);
        var service = new SearchDiagnosticsService(new BasicTokenizer(), _ => reader);
        const string query = "annual report notes missing";

        var inspected = service.Inspect(query);

        Assert.Equal(service.Tokenize(query), inspected.Select(diagnostic => diagnostic.Token));
        Assert.Contains(inspected, diagnostic => diagnostic.MatchCount > 1);
        foreach (var diagnostic in inspected)
        {
            var items = reader.Get(diagnostic.Token);
            Assert.Equal(items.Count, diagnostic.MatchCount);
            Assert.Equal(items.Take(3).Select(item => item.Name), diagnostic.SampleNames);
        }
    }

    private static FileIndex Left() => FileIndex.Build(@"C:\", Root,
    [
        Folder(Root, Root, @"C:\"),
        Folder(16, Root, "Visible"),
        Folder(17, Root, "Hidden"),
        File(18, 16, "report.txt"),
        File(19, 17, "report.txt"),
        File(20, 16, "annual report.pdf"),
        File(21, 17, "report draft.md"),
        File(22, 16, "notes.txt"),
        File(23, 17, "annual notes.txt")
    ]);

    private static FileIndex Right() => FileIndex.Build(@"D:\", Root,
    [
        Folder(Root, Root, @"D:\"),
        File(16, Root, "Report 2024.xlsx"),
        File(17, Root, "reporter.txt"),
        Folder(18, Root, "report")
    ]);

    private static IndexEntry Folder(ulong id, ulong parent, string name) =>
        File(id, parent, name, FileAttributes.Directory);

    private static IndexEntry File(ulong id, ulong parent, string name, FileAttributes attributes = FileAttributes.Normal) =>
        new(id, parent, name, attributes, 20, DateTime.UnixEpoch.Ticks);
}
