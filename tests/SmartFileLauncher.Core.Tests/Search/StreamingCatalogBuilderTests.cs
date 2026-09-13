using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class StreamingCatalogBuilderTests
{
    [Fact]
    public void StreamingOutputMatchesExistingFormatAndPreservesQueryAndUpdateContracts()
    {
        using var workspace = new TemporaryDirectory();
        var tokenizer = new BasicTokenizer();
        var root = new SearchItem("Root", @"C:\Root", true, null, null, null, 0, "");
        var folder = new SearchItem("İş 📁", @"C:\Root\İş 📁", true, null, null, null, 0, root.FullPath);
        var items = new List<SearchItem> { root, folder };
        for (var index = 0; index < 1000; index++)
            items.Add(new($"rapor {index}.txt", $@"{folder.FullPath}\rapor {index}.txt", false,
                index == 0 ? null : index, new DateTime(638000000000000001 + index, DateTimeKind.Utc),
                new DateTime(638000000000000002 + index, DateTimeKind.Local), index % 5, folder.FullPath));
        items.Add(new("", @"C:\Root\blank", false, null, null, null, 0, root.FullPath));
        items.Add(new("Case.txt", @"c:\root\Case.txt", false, long.MaxValue,
            new DateTime(638000000000000001, DateTimeKind.Unspecified), null, 9, @"c:\root"));
        var baseline = CompactSearchState.Create(items, tokenizer);
        var expectedPath = Path.Combine(workspace.Path, "expected.bin");
        baseline.WriteNewBase(expectedPath);
        using var builder = new StreamingCatalogBuilder(Path.Combine(workspace.Path, "scratch"), tokenizer);
        foreach (var item in items)
        {
            var parent = item == root ? -1 : item.ParentPath!.Equals(folder.FullPath, StringComparison.OrdinalIgnoreCase) ? 1 : 0;
            builder.Add(item, parent, parent < 0 ? null : items[parent].FullPath);
        }
        var actualPath = Path.Combine(workspace.Path, "actual.bin");
        var actual = builder.Complete(actualPath);
        Assert.Equal(File.ReadAllBytes(expectedPath), File.ReadAllBytes(actualPath));
        foreach (var item in items) { Assert.True(actual.TryGetItem(item.FullPath, out var found)); Assert.Equal(item, found); }
        Assert.Equal(Ordered(baseline.Get("rapor")), Ordered(actual.Get("rapor")));
        Assert.Equal(Ordered(baseline.GetPartial("rap")), Ordered(actual.GetPartial("rap")));
        Assert.Equal(Ordered(baseline.GetFuzzy("rappr", 1)), Ordered(actual.GetFuzzy("rappr", 1)));
        Assert.Equal(Ordered(baseline.GetDescendants(folder)), Ordered(actual.GetDescendants(folder)));
        var updated = actual.WithRecordUpserts([items[2] with { OpenCount = 99 }], tokenizer);
        Assert.Equal(99, updated.Get("rapor").Single(item => item.FullPath == items[2].FullPath).OpenCount);
        Assert.Equal(0, actual.Get("rapor").Single(item => item.FullPath == items[2].FullPath).OpenCount);
        Assert.Equal(Ordered(baseline.WithoutPathAndDescendants(folder.FullPath).GetAllItems()),
            Ordered(actual.WithoutPathAndDescendants(folder.FullPath).GetAllItems()));
    }

    [Fact]
    public void EmptyCatalogAndCancellationDoNotPublishInvalidCatalog()
    {
        using var workspace = new TemporaryDirectory();
        using var empty = new StreamingCatalogBuilder(Path.Combine(workspace.Path, "empty"), new BasicTokenizer());
        Assert.Empty(empty.Complete(Path.Combine(workspace.Path, "empty.bin")).GetAllItems());
        using var canceled = new StreamingCatalogBuilder(Path.Combine(workspace.Path, "cancel"), new BasicTokenizer());
        var path = Path.Combine(workspace.Path, "canceled.bin");
        Assert.Throws<OperationCanceledException>(() => canceled.Complete(path, new CancellationToken(true)));
        Assert.False(File.Exists(path));
    }

    private static SearchItem[] Ordered(IEnumerable<SearchItem> items) => items.OrderBy(item => item.FullPath, StringComparer.Ordinal).ToArray();
}
