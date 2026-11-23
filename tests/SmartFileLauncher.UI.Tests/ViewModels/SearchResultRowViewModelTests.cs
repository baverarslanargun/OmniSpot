using System.Collections.ObjectModel;
using System.Collections.Specialized;
using SmartFileLauncher.UI.ViewModels;
using Xunit;

namespace SmartFileLauncher.UI.Tests.ViewModels;

public sealed class SearchResultRowViewModelTests
{
    [Fact]
    public void AppendingAPageKeepsFullRowsAndOnlyReplacesTheOpenLastRow()
    {
        var items = Items(10);
        var rows = new ObservableCollection<SearchResultRowViewModel>();
        SearchResultRowViewModel.Sync(items, 4, rows);
        var before = rows.ToArray();
        var changes = Track(rows);

        items.AddRange(Items(7, start: 10));
        SearchResultRowViewModel.Sync(items, 4, rows);

        AssertLayout(items, 4, rows);
        Assert.Same(before[0], rows[0]);
        Assert.Same(before[1], rows[1]);
        Assert.NotSame(before[2], rows[2]);
        Assert.Equal(
            new[] { NotifyCollectionChangedAction.Replace, NotifyCollectionChangedAction.Add, NotifyCollectionChangedAction.Add },
            changes);
    }

    [Fact]
    public void ARemovalShiftsOnlyTheRowsAfterIt()
    {
        var items = Items(13);
        var rows = new ObservableCollection<SearchResultRowViewModel>();
        SearchResultRowViewModel.Sync(items, 4, rows);
        var before = rows.ToArray();

        items.RemoveAt(5);
        SearchResultRowViewModel.Sync(items, 4, rows);

        AssertLayout(items, 4, rows);
        Assert.Same(before[0], rows[0]);
        Assert.Equal(3, rows.Count);
    }

    [Fact]
    public void AnEmptyListClearsEveryRowAtOnce()
    {
        var rows = new ObservableCollection<SearchResultRowViewModel>();
        SearchResultRowViewModel.Sync(Items(9), 4, rows);
        var changes = Track(rows);

        SearchResultRowViewModel.Sync([], 4, rows);

        Assert.Empty(rows);
        Assert.Equal(new[] { NotifyCollectionChangedAction.Reset }, changes);
    }

    [Fact]
    public void RandomEditsAlwaysLeaveTheSameLayoutAsAFreshChunking()
    {
        var random = new Random(20261117);
        var items = Items(37);
        var rows = new ObservableCollection<SearchResultRowViewModel>();
        for (var step = 0; step < 400; step++)
        {
            switch (random.Next(5))
            {
                case 0: items.AddRange(Items(random.Next(1, 30), start: 1_000 * (step + 1))); break;
                case 1 when items.Count > 0: items.RemoveAt(random.Next(items.Count)); break;
                case 2: items.Insert(random.Next(items.Count + 1), Items(1, start: 100_000 + step)[0]); break;
                case 3 when items.Count > 1:
                    var from = random.Next(items.Count);
                    var moved = items[from];
                    items.RemoveAt(from);
                    items.Insert(random.Next(items.Count + 1), moved);
                    break;
                case 4 when items.Count > 0: items[random.Next(items.Count)] = Items(1, start: 200_000 + step)[0]; break;
            }
            var before = rows.ToArray();
            SearchResultRowViewModel.Sync(items, 4, rows);
            AssertLayout(items, 4, rows);
            for (var row = 0; row < Math.Min(before.Length, rows.Count); row++)
                if (before[row].Items.SequenceEqual(rows[row].Items)) Assert.Same(before[row], rows[row]);
        }
    }

    [Fact]
    public void ColumnsMustBePositive() =>
        Assert.Throws<ArgumentOutOfRangeException>(() => SearchResultRowViewModel.Sync(Items(1), 0, []));

    private static void AssertLayout(
        IReadOnlyList<SearchResultViewModel> items, int columns, IReadOnlyList<SearchResultRowViewModel> rows)
    {
        Assert.Equal(items.Chunk(columns).Count(), rows.Count);
        Assert.All(rows.Take(rows.Count - 1), row => Assert.Equal(columns, row.Items.Count));
        Assert.Equal(items, rows.SelectMany(row => row.Items));
    }

    private static List<NotifyCollectionChangedAction> Track(ObservableCollection<SearchResultRowViewModel> rows)
    {
        var changes = new List<NotifyCollectionChangedAction>();
        rows.CollectionChanged += (_, e) => changes.Add(e.Action);
        return changes;
    }

    private static List<SearchResultViewModel> Items(int count, int start = 0) =>
        Enumerable.Range(start, count)
            .Select(i => new SearchResultViewModel { Name = $"dosya-{i}", FullPath = $@"C:\Test\dosya-{i}" })
            .ToList();
}
