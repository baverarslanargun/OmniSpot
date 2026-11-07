using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Search.Paged;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class LiteralNameGramFilterTests
{
    [Fact]
    public void GramCandidatesPreserveLiteralResultsFiltersAndEverySort()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        var queries = new[] { "b", "needle", "NEEDLE", "bin", "ne", "ışık", "absent", "wide", "duplicate", "Root", "A", "[", "İ", "k" };
        var filters = new[]
        {
            ItemFilter.None,
            new ItemFilter(Kind: FilterItemKind.FilesOnly),
            new ItemFilter(Kind: FilterItemKind.FoldersOnly),
            new ItemFilter(Date: FilterDateRange.LastWeek),
            new ItemFilter(Size: FilterSizeRange.Large),
            new ItemFilter(Kind: FilterItemKind.FilesOnly, Size: FilterSizeRange.Large)
        };
        var views = filters.SelectMany(filter => Enum.GetValues<SortField>()
            .SelectMany(field => new[] { false, true }.Select(descending =>
                new ResultView(filter, new ItemSort(field, descending))))).ToArray();
        var expected = queries.SelectMany(query => views.SelectMany(view =>
            new[] { 1, 7, 100 }.Select(limit =>
                (Query: query, View: view, Limit: limit, Results: catalog.SearchLiteralNames(query, limit, view, default))))).ToArray();
        catalog.PrepareLiteralNameIndex(default);
        foreach (var sample in expected)
        {
            var actual = catalog.SearchLiteralNames(sample.Query, sample.Limit, sample.View, default);
            Assert.Equal(sample.Results.Select(item => (item.FullPath, item.Score)),
                actual.Select(item => (item.FullPath, item.Score)));
        }
        Assert.NotEmpty(catalog.SearchLiteralNames("needle", 100, ResultView.SearchDefault, default));
    }

    [Fact]
    public void PreparationAndSortCancellationAllowRetryAndExcludeRoot()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        Assert.Throws<OperationCanceledException>(() => catalog.PrepareLiteralNameIndex(cancelled.Token));
        catalog.PrepareLiteralNameIndex(default);
        var view = new ResultView(ItemFilter.None, new ItemSort(SortField.Size));
        Assert.Throws<OperationCanceledException>(() => catalog.SearchLiteralNames("b", 7, view, cancelled.Token));
        Assert.Equal(7, catalog.SearchLiteralNames("b", 7, view, default).Count);
        Assert.Single(catalog.SearchLiteralNames("Root", 10, ResultView.SearchDefault, default));
        Assert.Empty(catalog.SearchLiteralNames("Root", 10, ResultView.SearchDefault, default, excludeRoot: true));
    }

    [Fact]
    public void NewGenerationDoesNotReuseOldNameMasksOrChangeRetainedReaders()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        catalog.PrepareLiteralNameIndex(default);
        using var store = new CatalogStore(catalog, writable: true);
        store.Apply([new Change(10, 5, "needle-0000.pdf", Fixture.Entry(10, "fresh-name.pdf"))], 1);
        using var next = store.Acquire();
        next.PrepareLiteralNameIndex(default);
        Assert.Empty(catalog.SearchLiteralNames("fresh-name", 10, ResultView.SearchDefault, default));
        Assert.Single(next.SearchLiteralNames("fresh-name", 10, ResultView.SearchDefault, default));
        Assert.Single(catalog.SearchLiteralNames("needle-0000", 10, ResultView.SearchDefault, default));
        Assert.Empty(next.SearchLiteralNames("needle-0000", 10, ResultView.SearchDefault, default));
    }

    [Fact]
    public void IncrementalIndexMatchesAFreshBuildAcrossSuccessiveGenerations()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        catalog.PrepareLiteralNameIndex(default);
        using var store = new CatalogStore(catalog, writable: true);
        var generations = new List<Change[]>
        {
            new[]
            {
                new Change(20, 5, "ordinary-0010.bin", Fixture.Entry(20, "needle-renamed.pdf")),
                new Change(30, 5, "ordinary-0020.bin", null),
                new Change(5000, 5, "duplicate.bin", Fixture.Entry(5000, "duplicate.bin")),
                new Change(40, 5, "ordinary-0030.bin", Fixture.Entry(40, "ordinary-0030.bin") with { ParentFileReference = 6 }),
                new Change(50, 5, "ordinary-0040.bin", Fixture.Entry(50, "ordinary-0040.bin") with { SizeBytes = 7 }),
                new Change(5001, 5, "ışık-yeni-needle.txt", Fixture.Entry(5001, "ışık-yeni-needle.txt")),
                new Change(5002, 5, "wide-\ud801-new.txt", Fixture.Entry(5002, "wide-\ud801-new.txt")),
                new Change(5003, 5, "needle-dir", Fixture.Entry(5003, "needle-dir") with { IsDirectory = true, Attributes = FileAttributes.Directory })
            },
            Enumerable.Range(0, 1200)
                .Select(index => new Change((ulong)(6000 + index), 5, $"needle-mass-{index:0000}.pdf",
                    Fixture.Entry((ulong)(6000 + index), $"needle-mass-{index:0000}.pdf")))
                .Append(new Change(5000, 5, "duplicate.bin", null))
                .Append(new Change(5003, 5, "needle-dir", Fixture.Entry(5003, "a-needle-dir") with { IsDirectory = true, Attributes = FileAttributes.Directory }))
                .ToArray(),
            Enumerable.Range(0, 600)
                .Select(index => new Change((ulong)(6000 + index * 2), 5, $"needle-mass-{index * 2:0000}.pdf", null))
                .Append(new Change(6001, 5, "needle-mass-0001.pdf", Fixture.Entry(6001, "zz-needle-mass-0001.pdf")))
                .Append(new Change(1020, 6, "duplicate.bin", Fixture.Entry(1020, "duplicate.bin") with { ParentFileReference = 7, SizeBytes = 1 }))
                .Append(new Change(60, 5, "ordinary-0050.bin", Fixture.Entry(60, "ordinary-0050.bin") with { LastWriteTimeUtc = DateTime.UtcNow.AddYears(-3).Ticks }))
                .ToArray()
        };
        foreach (var query in LiteralQueries) foreach (var view in LiteralViews) catalog.SearchLiteralNames(query, 7, view, default);
        var current = catalog.Retain();
        try
        {
            var cursor = 1L;
            foreach (var changes in generations)
            {
                store.Apply(changes, cursor++);
                var next = store.Acquire();
                next.ReuseLiteralNameIndex(current);
                var stages = new List<string>();
                next.PrepareLiteralNameIndex(default, timing: (stage, _) => stages.Add(stage));
                Assert.Contains("literal-derive", stages);
                current.Dispose();
                current = next;
                using var fresh = new Catalog(Image.Open(next.Data.Directory, next.Data.Frame));
                fresh.PrepareLiteralNameIndex(default);
                AssertSameLiteralResults(fresh, next);
            }
            Assert.Single(current.SearchLiteralNames("zz-needle-mass-0001", 10, ResultView.SearchDefault, default));
            Assert.Empty(current.SearchLiteralNames("needle-mass-0000", 10, ResultView.SearchDefault, default));
        }
        finally { current.Dispose(); }
    }

    [Fact]
    public void LargeChangeSetsFallBackToAFullRebuild()
    {
        using var fixture = new Fixture();
        using var catalog = fixture.Build();
        catalog.PrepareLiteralNameIndex(default);
        using var store = new CatalogStore(catalog, writable: true);
        store.Apply(Enumerable.Range(0, 5000)
            .Select(index => new Change((ulong)(9000 + index), 5, $"bulk-{index:0000}.bin", Fixture.Entry((ulong)(9000 + index), $"bulk-{index:0000}.bin")))
            .ToArray(), 1);
        using var next = store.Acquire();
        next.ReuseLiteralNameIndex(catalog);
        var stages = new List<string>();
        next.PrepareLiteralNameIndex(default, timing: (stage, _) => stages.Add(stage));
        Assert.DoesNotContain("literal-derive", stages);
        Assert.Contains("literal-copy", stages);
        using var fresh = new Catalog(Image.Open(next.Data.Directory, next.Data.Frame));
        fresh.PrepareLiteralNameIndex(default);
        AssertSameLiteralResults(fresh, next);
    }

    private static readonly string[] LiteralQueries =
        ["needle", "NEEDLE", "mass", "duplicate", "b", "ne", "ışık", "wide", "absent", "A", "pdf", "0010", "renamed", "dir", "bulk"];

    private static readonly ResultView[] LiteralViews = new[]
        {
            ItemFilter.None,
            new ItemFilter(Kind: FilterItemKind.FilesOnly),
            new ItemFilter(Kind: FilterItemKind.FoldersOnly),
            new ItemFilter(Size: FilterSizeRange.Large)
        }
        .SelectMany(filter => Enum.GetValues<SortField>()
            .SelectMany(field => new[] { false, true }.Select(descending => new ResultView(filter, new ItemSort(field, descending)))))
        .ToArray();

    private static void AssertSameLiteralResults(Catalog expected, Catalog actual)
    {
        foreach (var query in LiteralQueries)
        foreach (var view in LiteralViews)
        foreach (var limit in new[] { 1, 7, 100 })
        {
            var want = expected.SearchLiteralNames(query, limit, view, default).Select(item => item.FullPath).ToArray();
            var got = actual.SearchLiteralNames(query, limit, view, default).Select(item => item.FullPath).ToArray();
            Assert.True(want.SequenceEqual(got),
                $"{query} {view.Sort.Field} desc={view.Sort.Descending} {view.Filter} limit={limit}: [{string.Join(", ", want)}] != [{string.Join(", ", got)}]");
        }
    }

    private sealed class Fixture : IDisposable
    {
        private readonly string _directory = Path.Combine(Path.GetTempPath(), "OmniSpot.LiteralGram.Tests", Guid.NewGuid().ToString("N"));

        internal Catalog Build()
        {
            var catalog = Catalog.Create(_directory, 5, 4096, "literal-gram-test", @"C:\Root");
            catalog.Accept(new NtfsMftEntry(5, 5, "Root", true, FileAttributes.Directory, 0, 0, 0));
            catalog.Accept(new NtfsMftEntry(6, 5, "b-parent-a", true, FileAttributes.Directory, 0, 0, 0));
            catalog.Accept(new NtfsMftEntry(7, 5, "b-parent-B", true, FileAttributes.Directory, 0, 0, 0));
            for (var index = 0; index < 2048; index++)
            {
                var name = index switch
                {
                    0 or 255 or 256 or 2047 => $"needle-{index:0000}.pdf",
                    512 => "ışık-needle.txt",
                    700 => "wide-\ud800-needle.txt",
                    1010 or 1011 => "duplicate.bin",
                    1012 => "DUPLICATE.BIN",
                    1100 => "A-boundary.txt",
                    1101 => "[boundary].txt",
                    1102 => "a-BOUNDARY.TXT",
                    1103 => "Kelvin-\u212a.txt",
                    1104 => "İ-ı.txt",
                    _ => $"ordinary-{index:0000}.bin"
                };
                var entry = Entry((ulong)(10 + index), name);
                if (index is >= 1010 and <= 1012)
                    entry = entry with { ParentFileReference = index == 1011 ? 7UL : 6UL, SizeBytes = 123,
                        LastWriteTimeUtc = DateTime.UtcNow.Date.Ticks };
                if (index == 256) entry = entry with { IsDirectory = true, Attributes = FileAttributes.Directory };
                catalog.Accept(entry);
            }
            catalog.Finish(buildNameOrder: false);
            return catalog;
        }

        internal static NtfsMftEntry Entry(ulong id, string name) => new(
            id, 5, name, false, FileAttributes.Archive, checked((long)id * 1024 * 1024),
            DateTime.UtcNow.AddDays(-(long)(id % 30)).Ticks, DateTime.UtcNow.AddDays(-(long)(id % 30)).Ticks);

        public void Dispose()
        {
            if (Directory.Exists(_directory)) Directory.Delete(_directory, recursive: true);
        }
    }
}
