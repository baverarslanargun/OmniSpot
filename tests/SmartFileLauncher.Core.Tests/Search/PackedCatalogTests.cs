using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class PackedCatalogTests
{
    [Fact]
    public void PackedRoundTripPreservesCanonicalMetadataAndSearchBehavior()
    {
        using var directory = new TemporaryDirectory();
        var records = Records();
        var path = Path.Combine(directory.Path, "packed.bin");
        var catalog = Build(directory.Path, path, records);
        var state = CompactSearchState.FromCatalog(catalog);
        var baseline = CompactSearchState.Create(records.Select(record => record.Item), new BasicTokenizer());
        foreach (var reopened in new[] { catalog, PackedCatalog.Open(path) })
        {
            Assert.Equal(records.Length, reopened.ItemCount);
            for (var i = 0; i < records.Length; i++)
            {
                Assert.Equal(records[i], reopened.GetRecord(i));
                Assert.Equal(i, reopened.Find(records[i].Item.FullPath.ToUpperInvariant()));
                Assert.Equal(records[i].Item, reopened.GetTransientItem(i, new()));
            }
            var actual = CompactSearchState.FromCatalog(reopened);
            foreach (var query in new[] { "txt", "rapor", "İş", "📁", "case", "", "\ud800", "RAP", "missing" })
            {
                Assert.Equal(Ordered(baseline.Get(query)), Ordered(actual.Get(query)));
                Assert.Equal(Ordered(baseline.GetPartial(query)), Ordered(actual.GetPartial(query)));
                Assert.Equal(Ordered(baseline.GetFuzzy(query, 1)), Ordered(actual.GetFuzzy(query, 1)));
            }
            Assert.Equal(Ordered(baseline.GetRoots()), Ordered(actual.GetRoots()));
            foreach (var record in records.Where(record => record.Item.IsDirectory))
                Assert.Equal(Ordered(baseline.GetChildren(record.Item.FullPath)), Ordered(actual.GetChildren(record.Item.FullPath)));
            var filter = new QueryCatalogFilter(true, true, false, true, null, null, null, null, 0, 1);
            Assert.Equal(Ordered(((IQueryCatalogSnapshot)baseline).GetItems(filter)), Ordered(((IQueryCatalogSnapshot)actual).GetItems(filter)));
        }
        var updated = state.WithRecordUpserts([records[2].Item with { OpenCount = 99 }], new BasicTokenizer());
        Assert.Equal(99, updated.Get("rapor").Single(item => item.FullPath == records[2].Item.FullPath).OpenCount);
        Assert.Equal(records[2].Item.OpenCount, state.Get("rapor").Single(item => item.FullPath == records[2].Item.FullPath).OpenCount);
        Assert.True(new FileInfo(path).Length < 100_000);
    }

    [Fact]
    public void ChecksumRejectsDamageAndEmptyAndCanceledBuildsAreHandled()
    {
        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "empty.bin");
        using (var builder = new PackedCatalogBuilder(Path.Combine(directory.Path, "empty"), new BasicTokenizer()))
            Assert.Equal(0, builder.Complete(path).ItemCount);
        var bytes = File.ReadAllBytes(path);
        bytes[80] ^= 1;
        var damaged = Path.Combine(directory.Path, "damaged.bin");
        File.WriteAllBytes(damaged, bytes);
        Assert.Throws<InvalidDataException>(() => PackedCatalog.Open(damaged));
        using var canceled = new PackedCatalogBuilder(Path.Combine(directory.Path, "cancel"), new BasicTokenizer());
        Assert.Throws<OperationCanceledException>(() => canceled.Complete(Path.Combine(directory.Path, "cancel.bin"), ct: new(true)));
    }

    [Fact]
    public void FuzzyTokenMatchingAgreesWithLegacyAcrossEditsAndUnicode()
    {
        using var directory = new TemporaryDirectory();
        var words = new[] { "A", "abc", "ABC", "abd", "xabc", "ab", "abcd", "İş", "iş", "café", "cafe", "📁", "\ud800" };
        var records = words.Select((word, i) => new PackedRecord(new(word, $@"C:\{i}", false, i, null, null, 0, null), 0, 0, false, false)).ToArray();
        var packed = CompactSearchState.FromCatalog(Build(directory.Path, Path.Combine(directory.Path, "fuzzy.bin"), records));
        var old = CompactSearchState.Create(records.Select(record => record.Item), new BasicTokenizer());
        foreach (var query in words.Concat(["ac", "bca", "ABCx", "caff", "i", "é", "a"]))
            for (var distance = 0; distance <= 3; distance++)
                Assert.Equal(Ordered(old.GetFuzzy(query, distance)), Ordered(packed.GetFuzzy(query, distance)));
    }

    internal static PackedCatalog Build(string workspace, string path, PackedRecord[] records)
    {
        using var builder = new PackedCatalogBuilder(Path.Combine(workspace, Guid.NewGuid().ToString("N")), new BasicTokenizer());
        var directories = new Dictionary<string, (int Id, string Path)>(StringComparer.OrdinalIgnoreCase);
        foreach (var record in records)
        {
            var parent = record.Item.ParentPath is { } p && directories.TryGetValue(p, out var found) ? found : (-1, (string?)null);
            var id = builder.Add(record, parent.Item1, parent.Item2);
            if (record.Item.IsDirectory) directories.Add(record.Item.FullPath, (id, record.Item.FullPath));
        }
        return builder.Complete(path);
    }

    internal static PackedRecord[] Records()
    {
        var root = new SearchItem("Root", @"C:\Root", true, null, null, null, 0, "");
        var folder = new SearchItem("İş 📁", @"C:\Root\İş 📁", true, null, null, null, 0, root.FullPath);
        var items = new List<SearchItem> { root, folder };
        for (var i = 0; i < 40; i++) items.Add(new($"rapor {i}.txt", $@"{folder.FullPath}\rapor {i}.txt", false,
            i == 0 ? null : i, new DateTime(638000000000000001 + i, (DateTimeKind)(i % 3)),
            new DateTime(638000000000000002 + i, (DateTimeKind)((i + 1) % 3)), i % 5, folder.FullPath));
        items.Add(new("", @"C:\Root\blank", false, null, null, null, 0, root.FullPath));
        items.Add(new("Case.txt", @"c:\root\Case.txt", false, long.MaxValue, null, null, int.MaxValue, @"c:\root"));
        items.Add(new("bad\ud800.txt", "C:\\Root\\bad\ud800.txt", false, -1, null, null, -1, root.FullPath));
        return items.Select((item, i) => new PackedRecord(item, 638000000000000000 + i, 637000000000000000 + i,
            i % 2 == 0, i % 3 == 0, 638100000000000000 + i)).ToArray();
    }
    private static SearchItem[] Ordered(IEnumerable<SearchItem> items) => items.OrderBy(item => item.FullPath, StringComparer.Ordinal).ToArray();
}
