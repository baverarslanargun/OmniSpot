using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.Search.Paged;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class IndexSorterTests
{
    [Theory]
    [InlineData(IndexSortMethod.Pivot, 1)]
    [InlineData(IndexSortMethod.Pivot, 4)]
    [InlineData(IndexSortMethod.Pivot, 16)]
    [InlineData(IndexSortMethod.Merge, 1)]
    [InlineData(IndexSortMethod.Merge, 4)]
    [InlineData(IndexSortMethod.Merge, 16)]
    public void AlgorithmsPreserveTheFullComparisonOrder(IndexSortMethod method, int workers)
    {
        var values = Enumerable.Range(0, 24000).Reverse().Select(i => (Name: i % 97, Id: i)).ToArray();
        Comparison<(int Name, int Id)> compare = (a, b) => a.Name != b.Name ? a.Name.CompareTo(b.Name) : a.Id.CompareTo(b.Id);
        var expected = values.ToArray();
        Array.Sort(expected, compare);
        IndexSorter.Sort(values, compare, default, new(SortMethod: method, AdaptiveSort: false, SortWorkers: workers));
        Assert.Equal(expected, values);
    }

    [Theory]
    [InlineData(IndexSortMethod.Pivot)]
    [InlineData(IndexSortMethod.Merge)]
    public void AdaptiveSelectionPreservesResultsAndCancellationIsObserved(IndexSortMethod method)
    {
        var values = Enumerable.Range(0, 18000).Reverse().ToArray();
        var workers = IndexSorter.Sort(values, (a, b) => a.CompareTo(b), default, new(SortMethod: method));
        Assert.InRange(workers, 1, 16);
        Assert.Equal(Enumerable.Range(0, values.Length), values);
        using var cancel = new CancellationTokenSource();
        cancel.Cancel();
        Assert.Throws<OperationCanceledException>(() => IndexSorter.Sort(values, (a, b) => a.CompareTo(b), cancel.Token));
    }
}
