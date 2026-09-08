using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class QueryCatalogFilterTests
{
    private static readonly QueryCatalogFilter Filter = new(
        IncludeFiles: true,
        IncludeDirectories: true,
        FilterDirectoryDates: false,
        FilterFileSize: true,
        CreatedAfter: new DateTime(2026, 7, 1),
        CreatedBeforeExclusive: new DateTime(2026, 8, 1),
        ModifiedAfter: null,
        ModifiedBeforeExclusive: null,
        MinSizeMb: 1,
        MaxSizeMb: 3);

    [Fact]
    public void MetadataBoundsKeepExistingInclusiveAndExclusiveSemantics()
    {
        Assert.True(Filter.Matches(false, 1024L * 1024, new DateTime(2026, 7, 1), null));
        Assert.True(Filter.Matches(false, 3L * 1024 * 1024, new DateTime(2026, 7, 31), null));
        Assert.False(Filter.Matches(false, 2L * 1024 * 1024, new DateTime(2026, 8, 1), null));
        Assert.False(Filter.Matches(false, null, new DateTime(2026, 7, 10), null));
        Assert.False(Filter.Matches(false, 0, new DateTime(2026, 7, 10), null));
    }

    [Fact]
    public void ExpansionDirectoryBypassesDateAndSizeUntilItsChildrenAreVisited()
    {
        Assert.True(Filter.Matches(true, null, null, null));
        Assert.False((Filter with { FilterDirectoryDates = true }).Matches(true, null, null, null));
    }

    [Fact]
    public void CompactFilterEnumerationIsLazyAndDoesNotPopulateQueryCaches()
    {
        var parent = new FileSystemNode("Data", @"C:\Data", true);
        var child = new FileSystemNode("report.pdf", @"C:\Data\report.pdf", false)
        {
            Metadata = new FileMetadata
            {
                SizeBytes = 2L * 1024 * 1024,
                CreatedTime = new DateTime(2026, 7, 10)
            }
        };
        parent.AddChild(child);
        var state = CompactSearchState.Create([parent, child], new BasicTokenizer());
        var queryState = Assert.IsType<CompactSearchState>(((ISearchStateReader)state).ForQuery());
        var items = ((IQueryCatalogSnapshot)queryState).GetItems(Filter);

        Assert.Equal(0, queryState.QueryItemCacheCount);
        Assert.Equal(0, queryState.QueryPathCacheCount);

        Assert.Equal(
            new[] { parent.FullPath, child.FullPath },
            items.Select(item => item.FullPath));
        Assert.Equal(0, queryState.QueryItemCacheCount);
        Assert.Equal(0, queryState.QueryPathCacheCount);
    }
}
