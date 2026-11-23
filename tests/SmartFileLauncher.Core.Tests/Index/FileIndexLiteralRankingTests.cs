using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class FileIndexLiteralRankingTests
{
    private const ulong Root = 5;
    private static ResultView Ranked(params (string FullPath, double Boost)[] opened) =>
        ResultView.SearchDefault with { LiteralRanking = new LiteralRanking(opened) };

    [Fact]
    public void ExactNameIsFoundBeforeTheFirstWindowOfSubstringMatchesInScope()
    {
        var entries = new[]
        {
            Folder(Root, Root, @"C:\"),
            Folder(16, Root, "Visible"),
            Folder(17, Root, "Hidden"),
            File(18, 16, "cmd.exe"),
            File(19, 17, "cmd.exe"),
            Folder(20, 16, "cmd.exe")
        }.Concat(Enumerable.Range(30, 120).Select(i => File((ulong)i, 16, $"{i:D3}-cmd.exe")));
        using var index = FileIndex.Build(@"C:\", Root, entries);
        var view = Ranked((@"C:\Hidden\cmd.exe", 0.5), (@"C:\Visible\030-cmd.exe", 0.5)) with
        {
            Filter = new ItemFilter(Kind: FilterItemKind.FilesOnly)
        };
        var scope = new[] { @"C:\Visible" };

        var first = index.Search("cmd.exe", 0, 100, view, scope);
        var rest = index.Search("cmd.exe", 100, 100, view, scope);

        Assert.Equal(@"C:\Visible\cmd.exe", first[0].FullPath);
        Assert.Equal(5, first[0].Score);
        Assert.Equal(@"C:\Visible\030-cmd.exe", first[1].FullPath);
        Assert.Equal(2.5, first[1].Score);
        Assert.Equal(121, index.CountMatches("cmd.exe", view, scope));
        Assert.Equal(index.Search("cmd.exe", 200, view, scope).Select(x => x.FullPath),
            first.Concat(rest).Select(x => x.FullPath));
        Assert.All(first.Concat(rest), hit => Assert.StartsWith(@"C:\Visible\", hit.FullPath));
        Assert.Equal(@"C:\Visible\030-cmd.exe",
            index.Search("cmd.exe", 1, view with { Sort = ItemSort.NameAscending }, scope)[0].FullPath);
    }

    [Fact]
    public void EachRankAndHistoryBoostStaysInOrderAcrossVolumePages()
    {
        using var left = FileIndex.Build(@"C:\", Root,
        [
            Folder(Root, Root, @"C:\"),
            File(16, Root, "cmd.exe"),
            File(17, Root, "cmd", FileAttributes.Directory),
            File(18, Root, "cmd.tmp.txt"),
            File(19, Root, "CMDtool"),
            File(20, Root, "qcmd_cmdTool"),
            File(21, Root, "xxcmd"),
            File(22, Root, "z_cmdTool")
        ]);
        using var right = FileIndex.Build(@"D:\", Root,
        [
            Folder(Root, Root, @"D:\"),
            File(16, Root, "cmd.exe"),
            File(17, Root, "cmdTool"),
            File(18, Root, "xxcmd")
        ]);
        using var reader = new FileIndexReader([(right, null), (left, null)]);
        var view = Ranked((@"D:\cmd.exe", 0.25), (@"C:\z_cmdTool", 0.5), (@"D:\xxcmd", 0.5), (@"D:\missing_cmdTool", 0.5));
        var whole = reader.SearchLiteralNames("cmd", 50, view, default);
        var paged = Enumerable.Range(0, 4)
            .SelectMany(i => reader.SearchLiteralNames("cmd", i * 3, 3, view, default)).ToArray();

        Assert.Equal(new[] { 5, 4.25, 4, 3, 3, 3, 2.5, 2, 1.5, 1 }, whole.Select(hit => hit.Score));
        Assert.Equal(new[]
        {
            @"C:\cmd", @"D:\cmd.exe", @"C:\cmd.exe", @"C:\cmd.tmp.txt", @"C:\CMDtool", @"D:\cmdTool",
            @"C:\z_cmdTool", @"C:\qcmd_cmdTool", @"D:\xxcmd", @"C:\xxcmd"
        }, whole.Select(hit => hit.FullPath));
        Assert.Equal(whole.Select(hit => (hit.FullPath, hit.Score)), paged.Select(hit => (hit.FullPath, hit.Score)));
        Assert.Equal(whole.Count, reader.CountLiteralNames("cmd", view, default));
    }

    [Theory]
    [InlineData(".exe")]
    [InlineData("2026")]
    public void DenseLiteralRemainderMatchesTheReferenceRanking(string query)
    {
        var entries = new[]
        {
            Folder(Root, Root, @"C:\"),
            File(16, Root, ".exe-start-2026"),
            File(17, Root, "first.exec .exeMore_2026"),
            File(18, Root, "other .execute 2026"),
            File(19, Root, "2026.report.exe"),
            File(20, Root, "Z2026_then_2026.txt")
        }.Concat(Enumerable.Range(100, 20_000).Select(i => File((ulong)i, Root,
            $"{i:D5}-inventory-catalog-entry-for-the-parallel-name-scan-of-many-items-of2026.exe")));
        using var index = FileIndex.Build(@"C:\", Root, entries);
        var view = Ranked();
        var baseline = index.Search(query, 25_000, ResultView.SearchDefault);
        var expected = baseline.Select(hit => (hit.FullPath, Score: (double)LiteralRanking.Classify(hit.Name, query)))
            .OrderByDescending(hit => hit.Score).Skip(1).Take(63).ToArray();

        var ranked = index.Search(query, 1, 63, view, null).Select(hit => (hit.FullPath, hit.Score));
        Assert.Equal(expected, ranked);
        Assert.Equal(baseline.Count, index.CountMatches(query, view, null));
    }

    [Fact]
    public void OpenedRowsAreResolvedAgainAfterDirectoryRenameAndCompaction()
    {
        using var index = FileIndex.Build(@"C:\", Root,
        [
            Folder(Root, Root, @"C:\"),
            File(16, Root, "first-unrelated"),
            Folder(17, Root, "Parent"),
            File(18, 17, "cmdTool"),
            File(19, Root, "cmdBackup")
        ]);
        var view = Ranked((@"C:\Parent\cmdTool", 0.5), (@"C:\Moved\cmdTool", 0.25));
        Assert.Equal(3.5, index.Search("cmd", 2, view)[0].Score);

        index.Apply([IndexChange.Upsert(Folder(17, Root, "Moved"), Root, "Parent")]);
        Assert.Equal((@"C:\Moved\cmdTool", 3.25),
            index.Search("cmd", 1, view).Select(hit => (hit.FullPath, hit.Score)).Single());

        index.Apply([IndexChange.Delete(16, Root, "first-unrelated")]);
        Assert.Equal(3.25, index.Search("cmd", 1, view)[0].Score);
        index.CompactNow();
        Assert.Equal((@"C:\Moved\cmdTool", 3.25),
            index.Search("cmd", 1, view).Select(hit => (hit.FullPath, hit.Score)).Single());
    }

    [Fact]
    public void OpenedNameTiesKeepTheirPlacementAndCaseBetweenHistorySnapshots()
    {
        using var index = FileIndex.Build(@"C:\", Root,
        [
            Folder(Root, Root, @"C:\"),
            Folder(16, Root, "Bb"),
            Folder(17, Root, "Aa"),
            File(18, 16, "cmdTool"),
            File(19, 17, "cmdTool"),
            File(20, 17, "CMDtool"),
            File(21, Root, "cmdZulu")
        ]);
        var tied = Ranked((@"C:\Bb\cmdTool", 0.5), (@"C:\Aa\cmdTool", 0.5), (@"C:\cmdZulu", 0.5));
        Assert.Equal(@"C:\Aa\cmdTool", index.Search("cmd", 1, tied)[0].FullPath);

        var changed = Ranked((@"C:\Aa\CMDtool", 0.5));
        Assert.Equal(@"C:\Aa\CMDtool", index.Search("cmd", 1, changed)[0].FullPath);
    }

    private static IndexEntry Folder(ulong id, ulong parent, string name) =>
        File(id, parent, name, FileAttributes.Directory);

    private static IndexEntry File(ulong id, ulong parent, string name, FileAttributes attributes = FileAttributes.Normal) =>
        new(id, parent, name, attributes, 20, DateTime.UnixEpoch.Ticks);
}
