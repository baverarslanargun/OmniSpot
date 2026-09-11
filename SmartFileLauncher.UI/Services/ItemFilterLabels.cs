using SmartFileLauncher.Core.Filtering;

namespace SmartFileLauncher.UI.Services;

internal static class ItemFilterLabels
{
    internal static string Category(FileFilterCategory category) => category switch
    {
        FileFilterCategory.Document => "Belge",
        FileFilterCategory.Image => "Görsel",
        FileFilterCategory.Video => "Video",
        FileFilterCategory.Audio => "Ses",
        FileFilterCategory.Archive => "Arşiv",
        FileFilterCategory.Code => "Kod",
        FileFilterCategory.Application => "Uygulama",
        _ => string.Empty
    };

    internal static string Date(FilterDateRange date) => date switch
    {
        FilterDateRange.Today => "Bugün",
        FilterDateRange.LastWeek => "Son 7 gün",
        FilterDateRange.LastMonth => "Son 30 gün",
        FilterDateRange.LastYear => "Son 1 yıl",
        _ => string.Empty
    };

    internal static string Size(FilterSizeRange size) => size switch
    {
        FilterSizeRange.Small => "Küçük (<1 MB)",
        FilterSizeRange.Medium => "Orta (1-100 MB)",
        FilterSizeRange.Large => "Büyük (>100 MB)",
        _ => string.Empty
    };

    internal static string Kind(FilterItemKind kind) => kind switch
    {
        FilterItemKind.FoldersOnly => "Yalnız klasörler",
        FilterItemKind.FilesOnly => "Yalnız dosyalar",
        _ => string.Empty
    };

    internal static string SortField(SortField field) => field switch
    {
        Core.Filtering.SortField.Relevance => "İlgi düzeyi",
        Core.Filtering.SortField.Name => "Ad",
        Core.Filtering.SortField.Modified => "Değiştirilme tarihi",
        Core.Filtering.SortField.Size => "Boyut",
        Core.Filtering.SortField.Kind => "Tür",
        _ => string.Empty
    };

    internal static IReadOnlyList<(object Value, string Label)> Chips(ItemFilter filter)
    {
        ArgumentNullException.ThrowIfNull(filter);
        var chips = new List<(object, string)>();

        if (filter.Kind != FilterItemKind.Any)
        {
            chips.Add((filter.Kind, Kind(filter.Kind)));
        }

        foreach (var category in FileFilterCategories.All)
        {
            if ((filter.Categories & category) != 0)
            {
                chips.Add((category, Category(category)));
            }
        }

        if (filter.Date != FilterDateRange.Any)
        {
            chips.Add((filter.Date, Date(filter.Date)));
        }

        if (filter.Size != FilterSizeRange.Any)
        {
            chips.Add((filter.Size, SizeBadge(filter.Size)));
        }

        return chips;
    }

    internal static IReadOnlyList<string> Parts(ItemFilter filter) =>
        Chips(filter).Select(chip => chip.Label).ToList();

    internal static string Badge(ItemFilter filter)
    {
        var parts = Parts(filter);
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        return parts.Count == 1
            ? parts[0]
            : $"{parts[0]} +{parts.Count - 1}";
    }

    internal static string Tooltip(ItemFilter filter)
    {
        var parts = Parts(filter);
        if (parts.Count == 0)
        {
            return string.Empty;
        }

        return parts.Count == 1
            ? $"Etkin filtre: {parts[0]}"
            : $"Etkin filtreler: {string.Join(", ", parts)} — tümünü görmek için tıkla";
    }

    internal static ItemFilter Without(ItemFilter filter, object value)
    {
        ArgumentNullException.ThrowIfNull(filter);
        return value switch
        {
            FileFilterCategory category => filter with
            {
                Categories = filter.Categories & ~category
            },
            FilterDateRange => filter with { Date = FilterDateRange.Any },
            FilterSizeRange => filter with { Size = FilterSizeRange.Any },
            FilterItemKind => filter with { Kind = FilterItemKind.Any },
            _ => filter
        };
    }

    private static string SizeBadge(FilterSizeRange size) => size switch
    {
        FilterSizeRange.Small => "Küçük",
        FilterSizeRange.Medium => "Orta",
        FilterSizeRange.Large => "Büyük",
        _ => string.Empty
    };
}
