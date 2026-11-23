using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Search;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Search;

public sealed class LiteralRankingSessionTests
{
    [Fact]
    public async Task SearchAndLaterPagesUseTheSameOpenHistoryUntilTheNextSearch()
    {
        const string root = @"C:\RankTest";
        var entries = new List<IndexEntry>
        {
            new(5, 5, "root", FileAttributes.Directory, 0, 0),
            new(16, 5, "A", FileAttributes.Directory, 0, 0),
            new(17, 5, "Z", FileAttributes.Directory, 0, 0),
            new(18, 16, "cmd.exe", FileAttributes.Normal, 1, 0),
            new(19, 17, "cmd.exe", FileAttributes.Normal, 1, 0),
            new(20, 5, "appcmd.exe", FileAttributes.Normal, 1, 0)
        };
        using var index = FileIndex.Build(root, 5, entries);
        var engine = new SearchEngine(_ => new FileIndexReader([(index, null)]),
            new BasicTokenizer(), new BasicScoringStrategy(), literalNamesOnly: true);
        var ranking = LiteralRanking.Empty;
        var service = new SearchApplicationService(engine.Search, (_, _, _, _) => [],
            (_, _, _) => throw new InvalidOperationException(), _ => throw new InvalidOperationException(),
            engine.SearchPage, engine.CountMatches, () => ranking);
        var request = new SearchRequest("cmd.exe", false, true, MaxResults: 1);
        var first = await service.SearchAsync(request);
        var other = await service.LoadPageAsync(request, first, 1, 1);
        var firstPath = Assert.Single(first.Results).FullPath;
        var otherPath = Assert.Single(other).FullPath;
        Assert.NotEqual(firstPath, otherPath);
        Assert.Equal("cmd.exe", first.Results[0].Name);
        Assert.Equal("cmd.exe", other[0].Name);

        ranking = new LiteralRanking([(otherPath, 0.4), (Path.Combine(root, "appcmd.exe"), 0.5)]);
        var samePage = await service.LoadPageAsync(request, first, 1, 1);
        Assert.Equal(otherPath, Assert.Single(samePage).FullPath);
        Assert.Equal(firstPath, Assert.Single((await service.RefreshAsync(request, first)).Results).FullPath);
        Assert.Equal(3, await service.CountAsync(request, first));

        var next = await service.SearchAsync(request);
        Assert.Equal(otherPath, Assert.Single(next.Results).FullPath);
        Assert.Equal(firstPath, Assert.Single(await service.LoadPageAsync(request, next, 1, 1)).FullPath);
        Assert.Equal("appcmd.exe", Assert.Single((await service.SearchAsync(request with { Sort = ItemSort.NameAscending })).Results).Name);
    }
}
