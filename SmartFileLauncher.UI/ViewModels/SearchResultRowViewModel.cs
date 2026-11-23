using System.Collections.ObjectModel;

namespace SmartFileLauncher.UI.ViewModels;

public sealed class SearchResultRowViewModel(IReadOnlyList<SearchResultViewModel> items)
{
    public IReadOnlyList<SearchResultViewModel> Items { get; } = items;

    internal static void Sync(
        IReadOnlyList<SearchResultViewModel> items, int columns, ObservableCollection<SearchResultRowViewModel> rows)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(columns, 1);
        var count = (items.Count + columns - 1) / columns;
        if (count == 0)
        {
            if (rows.Count > 0) rows.Clear();
            return;
        }

        for (var row = 0; row < count; row++)
        {
            var start = row * columns;
            var take = Math.Min(columns, items.Count - start);
            if (row < rows.Count && Holds(rows[row].Items, items, start, take)) continue;
            var slice = new SearchResultViewModel[take];
            for (var i = 0; i < take; i++) slice[i] = items[start + i];
            var next = new SearchResultRowViewModel(slice);
            if (row < rows.Count) rows[row] = next;
            else rows.Add(next);
        }
        while (rows.Count > count) rows.RemoveAt(rows.Count - 1);
    }

    private static bool Holds(IReadOnlyList<SearchResultViewModel> row, IReadOnlyList<SearchResultViewModel> items, int start, int take)
    {
        if (row.Count != take) return false;
        for (var i = 0; i < take; i++)
            if (!ReferenceEquals(row[i], items[start + i])) return false;
        return true;
    }
}
