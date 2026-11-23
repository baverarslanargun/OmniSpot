using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Search.Paged;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class LiteralNameRankingTests
{
    [Fact]
    public void PreparedAndLinearSearchRankBeforeApplyingTheLimit()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OmniSpot.LiteralRanking.Tests", Guid.NewGuid().ToString("N"));
        try
        {
            using var catalog = Catalog.Create(directory, 5, 512, "literal-ranking-test", @"C:\root-cmd");
            catalog.Accept(Entry(5, 5, "root-cmd", directory: true));
            catalog.Accept(Entry(6, 5, "Zulu", directory: true));
            catalog.Accept(Entry(7, 5, "cmd"));
            catalog.Accept(Entry(8, 5, "CMD.EXE"));
            catalog.Accept(Entry(9, 5, "cmd_more.exe"));
            catalog.Accept(Entry(10, 6, "cmd_more.exe"));
            catalog.Accept(Entry(11, 5, "cmdx.exe"));
            catalog.Accept(Entry(12, 5, "aacmd-cmd.log"));
            catalog.Accept(Entry(13, 5, "zzcmd.log"));
            catalog.Accept(Entry(14, 5, "ÖLÇÜ.txt"));
            catalog.Accept(Entry(15, 5, "ölçü-ek.txt"));
            catalog.Accept(Entry(16, 5, "notlar_ölçü.txt"));
            catalog.Accept(Entry(17, 5, "xölçü.txt"));
            catalog.Accept(Entry(18, 5, "Türkçe-plain.txt"));
            catalog.Accept(Entry(19, 5, "ſolo.txt"));
            for (var index = 0; index < 150; index++)
                catalog.Accept(Entry((ulong)(100 + index), 5, $"{index:D4}cmd.exe"));
            catalog.Finish(buildNameOrder: false);

            var relevance = ResultView.SearchDefault with
            {
                LiteralRanking = new LiteralRanking(
                [
                    (@"C:\root-cmd\Zulu\cmd_more.exe", 0.25),
                    (@"C:\root-cmd\zzcmd.log", 0.5),
                    (@"C:\root-cmd\missing-cmd.txt", 0.5),
                    (@"D:\Other\cmd", 0.5)
                ])
            };
            void AssertSearchOrder()
            {
                var exact = Assert.Single(catalog.SearchLiteralNames("cmd.exe", 1, relevance, default));
                Assert.Equal(("CMD.EXE", 5d), (exact.Name, exact.Score));

                var topThree = catalog.SearchLiteralNames("cmd", 3, relevance, default, excludeRoot: true);
                Assert.Equal(
                    new[]
                    {
                        (@"C:\root-cmd\cmd", 5d),
                        (@"C:\root-cmd\CMD.EXE", 4d),
                        (@"C:\root-cmd\Zulu\cmd_more.exe", 3.25)
                    },
                    topThree.Select(item => (item.FullPath, item.Score)));

                var topEight = catalog.SearchLiteralNames("cmd", 8, relevance, default, excludeRoot: true);
                Assert.Equal(
                    new[]
                    {
                        ("cmd", 5d), ("CMD.EXE", 4d), ("cmd_more.exe", 3.25), ("cmdx.exe", 3d),
                        ("cmd_more.exe", 3d), ("aacmd-cmd.log", 2d), ("zzcmd.log", 1.5), ("0000cmd.exe", 1d)
                    },
                    topEight.Select(item => (item.Name, item.Score)));

                var unicode = catalog.SearchLiteralNames("ölçü", 4, relevance, default);
                Assert.Equal(
                    new[] { ("ÖLÇÜ.txt", 4d), ("ölçü-ek.txt", 3d), ("notlar_ölçü.txt", 2d), ("xölçü.txt", 1d) },
                    unicode.Select(item => (item.Name, item.Score)));

                var mixedName = Assert.Single(catalog.SearchLiteralNames("plain", 1, relevance, default));
                Assert.Equal(("Türkçe-plain.txt", 2d), (mixedName.Name, mixedName.Score));
                Assert.Empty(catalog.SearchLiteralNames("solo", 1, relevance, default));

                var folders = relevance with { Filter = new ItemFilter(Kind: FilterItemKind.FoldersOnly) };
                Assert.Equal("root-cmd", Assert.Single(catalog.SearchLiteralNames("cmd", 1, folders, default)).Name);
                Assert.Empty(catalog.SearchLiteralNames("cmd", 1, folders, default, excludeRoot: true));

                var byName = relevance with { Sort = ItemSort.NameAscending };
                var firstByName = Assert.Single(catalog.SearchLiteralNames("cmd.exe", 1, byName, default));
                Assert.Equal(("0000cmd.exe", 0d), (firstByName.Name, firstByName.Score));
            }

            AssertSearchOrder();
            catalog.PrepareLiteralNameIndex(default);
            AssertSearchOrder();
        }
        finally
        {
            if (Directory.Exists(directory)) Directory.Delete(directory, recursive: true);
        }
    }

    private static NtfsMftEntry Entry(ulong id, ulong parent, string name, bool directory = false) =>
        new(id, parent, name, directory, directory ? FileAttributes.Directory : FileAttributes.Archive, 0, 0, 0);
}
