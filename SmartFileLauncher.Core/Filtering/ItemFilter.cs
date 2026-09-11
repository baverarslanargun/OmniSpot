namespace SmartFileLauncher.Core.Filtering;

public enum FilterItemKind
{
    Any,
    FilesOnly,
    FoldersOnly
}

public enum FilterDateRange
{
    Any,
    Today,
    LastWeek,
    LastMonth,
    LastYear
}

public enum FilterSizeRange
{
    Any,
    Small,
    Medium,
    Large
}

public sealed record ItemFilter(
    FileFilterCategory Categories = FileFilterCategory.None,
    FilterDateRange Date = FilterDateRange.Any,
    FilterSizeRange Size = FilterSizeRange.Any,
    FilterItemKind Kind = FilterItemKind.Any)
{
    public const long SmallMaxBytes = 1L * 1024 * 1024;
    public const long MediumMaxBytes = 100L * 1024 * 1024;

    public static readonly ItemFilter None = new();

    public bool IsActive =>
        Categories != FileFilterCategory.None ||
        Date != FilterDateRange.Any ||
        Size != FilterSizeRange.Any ||
        Kind != FilterItemKind.Any;

    public bool HidesFolders => Kind == FilterItemKind.FilesOnly;

    public bool HidesFiles => Kind == FilterItemKind.FoldersOnly;

    public ItemFilter WithCategoryToggled(FileFilterCategory category) =>
        this with { Categories = Categories ^ category };

    public bool Matches(
        string name,
        bool isDirectory,
        long? sizeBytes,
        DateTime? lastWriteTime,
        DateTime now)
    {
        if (isDirectory)
        {
            return Kind != FilterItemKind.FilesOnly;
        }

        if (Kind == FilterItemKind.FoldersOnly)
        {
            return false;
        }

        if (Categories != FileFilterCategory.None &&
            !FileFilterCategories.Matches(name, Categories))
        {
            return false;
        }

        return MatchesSize(sizeBytes) && MatchesDate(lastWriteTime, now);
    }

    private bool MatchesSize(long? sizeBytes)
    {
        if (Size == FilterSizeRange.Any)
        {
            return true;
        }

        if (sizeBytes == null)
        {
            return false;
        }

        return Size switch
        {
            FilterSizeRange.Small => sizeBytes.Value < SmallMaxBytes,
            FilterSizeRange.Medium => sizeBytes.Value >= SmallMaxBytes && sizeBytes.Value <= MediumMaxBytes,
            FilterSizeRange.Large => sizeBytes.Value > MediumMaxBytes,
            _ => true
        };
    }

    private bool MatchesDate(DateTime? lastWriteTime, DateTime now)
    {
        if (Date == FilterDateRange.Any)
        {
            return true;
        }

        if (lastWriteTime == null)
        {
            return false;
        }

        var threshold = Date switch
        {
            FilterDateRange.Today => now.Date,
            FilterDateRange.LastWeek => now.AddDays(-7),
            FilterDateRange.LastMonth => now.AddDays(-30),
            FilterDateRange.LastYear => now.AddYears(-1),
            _ => DateTime.MinValue
        };

        return lastWriteTime.Value >= threshold;
    }
}
