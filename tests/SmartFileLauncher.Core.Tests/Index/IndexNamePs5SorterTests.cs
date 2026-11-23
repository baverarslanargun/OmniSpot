using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Index;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Index;

public sealed class IndexNamePs5SorterTests
{
    private const string RootPath = @"C:\Kok";
    private const ulong Root = 5UL << 48 | 5;

    [Theory]
    [InlineData(1)]
    [InlineData(8)]
    public void SortMatchesIndexNamesComparer(int workers)
    {
        var names = new IndexNames();
        foreach (var name in new[] { "a", "A", "a\0", "a\0b", "abcdef", "abcdefg", "abcdefgh", "ç", "Ç", "İ", "ı", "😀", "\ud800", "\ud801", "�" })
            names.Intern(name);
        for (var i = 0; i < 36000; i++)
        {
            names.Intern($"abcdefg.ortak-öneki-{i:D5}.txt");
            names.Intern($"ABCDEFG.ORTAK-ÖNEKİ-{i:D5}.TXT");
            names.Intern($"{i % 101:D3}-{i:D5}-🧪");
        }
        var actual = Enumerable.Range(0, names.Count).ToArray();
        new Random(6405).Shuffle(actual);
        var expected = (int[])actual.Clone();
        Array.Sort(expected, names.Compare);

        IndexNamePs5Sorter.Sort(actual, names, workers, CancellationToken.None);

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SortHonorsCancellationBeforeTouchingTheInput()
    {
        var names = new IndexNames();
        var ids = new[] { names.Intern("z"), names.Intern("a") };
        var token = new CancellationToken(true);

        Assert.Throws<OperationCanceledException>(() => IndexNamePs5Sorter.Sort(ids, names, 8, token));
        Assert.Equal(new[] { 0, 1 }, ids);
    }

    [Fact]
    public void WorkersFollowTheSortSettings()
    {
        var adaptive = Math.Min(8, Environment.ProcessorCount);

        Assert.Equal(adaptive, IndexNamePs5Sorter.Workers(null));
        Assert.Equal(adaptive, IndexNamePs5Sorter.Workers(new IndexBuildOptions(SortWorkers: 3)));
        Assert.Equal(3, IndexNamePs5Sorter.Workers(new IndexBuildOptions(AdaptiveSort: false, SortWorkers: 3)));
    }

    [Fact]
    public void Ps5IsTheDefaultAndIsNeverSentToTheService()
    {
        Assert.Equal(IndexSortMethod.Ps5, new IndexBuildOptions().SortMethod);
        Assert.Equal(IndexSortMethod.Pivot, new IndexBuildOptions().ForService().SortMethod);
        var merge = new IndexBuildOptions(SortMethod: IndexSortMethod.Merge, AdaptiveSort: false, SortWorkers: 5);
        Assert.Same(merge, merge.ForService());
    }

    [Theory]
    [InlineData(IndexSortMethod.Ps5)]
    [InlineData(IndexSortMethod.Pivot)]
    [InlineData(IndexSortMethod.Merge)]
    public void FileIndexKeepsTheReferenceNameOrderAfterBuildCompactionAndLoad(IndexSortMethod method)
    {
        var files = new List<IndexEntry>();
        for (var i = 0; i < 4000; i++)
        {
            files.Add(File(files.Count, $"abcdefg.ortak-öneki-{i:D5}-zq.txt"));
            files.Add(File(files.Count, $"ABCDEFG.ORTAK-ÖNEKİ-{i:D5}-ZQ.TXT"));
            files.Add(File(files.Count, $"{i % 101:D3}-{i:D5}-zq-🧪"));
        }
        var options = new IndexBuildOptions(SortMethod: method, AdaptiveSort: false, SortWorkers: 3);
        var sortedWith = 0;
        using var index = FileIndex.Build(RootPath, Root,
            files.Prepend(new IndexEntry(Root, Root, ".", FileAttributes.Directory, 0, 0)), CancellationToken.None, options,
            (_, workers) => sortedWith = workers);
        Assert.Equal(options, index.NameSort);
        Assert.Equal(3, sortedWith);
        AssertNameOrder(index, files);

        files = Delete(index, files, 1);
        index.CompactNow();
        AssertNameOrder(index, files);

        using var directory = new TemporaryDirectory();
        var path = Path.Combine(directory.Path, "index.idx");
        index.Save(path, new IndexCheckpoint(1, 1, 1));
        using var loaded = FileIndex.Load(path, out _);
        Assert.NotNull(loaded);
        loaded!.NameSort = options with { SortWorkers = 2 };
        files = Delete(loaded, files, 0);
        loaded.CompactNow();
        AssertNameOrder(loaded, files);
    }

    private static IndexEntry File(int at, string name) =>
        new(1UL << 48 | (uint)at + 1000UL, Root, name, FileAttributes.Archive, at, 0);

    private static List<IndexEntry> Delete(FileIndex index, List<IndexEntry> files, int remainder)
    {
        var removed = files.Where((_, at) => at % 3 == remainder).ToList();
        index.Apply(removed.Select(file => IndexChange.Delete(file.FileReference, Root, file.Name)).ToList());
        return files.Except(removed).ToList();
    }

    private static void AssertNameOrder(FileIndex index, IEnumerable<IndexEntry> files)
    {
        var names = new IndexNames();
        var ids = files.Select(file => names.Intern(file.Name)).ToArray();
        Array.Sort(ids, names.Compare);
        var expected = ids.Select(id => RootPath + @"\" + names.Text(id)).ToArray();

        var actual = index.Search("zq", 100_000, new ResultView(ItemFilter.None, ItemSort.NameAscending))
            .Select(result => result.FullPath).ToArray();

        Assert.Equal(expected, actual);
    }
}
