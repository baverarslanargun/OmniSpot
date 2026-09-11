namespace SmartFileLauncher.Core.Filtering;

public enum SortField
{
    Relevance,
    Name,
    Modified,
    Size,
    Kind
}

public sealed record ItemSort(
    SortField Field = SortField.Relevance,
    bool Descending = true)
{
    public static readonly ItemSort Relevance = new();
    public static readonly ItemSort NameAscending = new(SortField.Name, Descending: false);

    public bool IsRelevance => Field == SortField.Relevance;

    public static bool DefaultDescending(SortField field) => field switch
    {
        SortField.Name => false,
        SortField.Kind => false,
        _ => true
    };

    public int Compare(SortRow left, SortRow right)
    {
        if (Field is SortField.Size or SortField.Modified)
        {
            var leftMissing = IsMissing(left);
            var rightMissing = IsMissing(right);
            if (leftMissing != rightMissing)
            {
                return leftMissing ? 1 : -1;
            }

            if (leftMissing)
            {
                return Tiebreak(left, right);
            }
        }

        var primary = Field switch
        {
            SortField.Name => string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase),
            SortField.Kind => string.Compare(
                Extension(left),
                Extension(right),
                StringComparison.OrdinalIgnoreCase),
            SortField.Size => Size(left).CompareTo(Size(right)),
            SortField.Modified => left.LastWriteTime!.Value.CompareTo(right.LastWriteTime!.Value),
            _ => left.Score.CompareTo(right.Score)
        };

        return primary != 0
            ? Descending ? -primary : primary
            : Tiebreak(left, right);
    }

    private int Tiebreak(SortRow left, SortRow right)
    {
        if (!IsRelevance)
        {
            var byScore = right.Score.CompareTo(left.Score);
            if (byScore != 0)
            {
                return byScore;
            }
        }

        var byName = string.Compare(left.Name, right.Name, StringComparison.OrdinalIgnoreCase);
        if (byName != 0)
        {
            return byName;
        }

        var byPath = string.Compare(left.FullPath, right.FullPath, StringComparison.OrdinalIgnoreCase);
        return byPath != 0 ? byPath : string.CompareOrdinal(left.FullPath, right.FullPath);
    }

    private bool IsMissing(SortRow row) => Field == SortField.Size
        ? row.IsDirectory || row.SizeBytes == null
        : row.LastWriteTime == null;

    private static long Size(SortRow row) => row.SizeBytes ?? 0;

    private static string Extension(SortRow row) =>
        row.IsDirectory ? string.Empty : Path.GetExtension(row.Name);
}

public readonly record struct SortRow(
    string Name,
    string FullPath,
    bool IsDirectory,
    long? SizeBytes,
    DateTime? LastWriteTime,
    double Score);

public sealed record ResultView(ItemFilter Filter, ItemSort Sort)
{
    public static readonly ResultView SearchDefault = new(ItemFilter.None, ItemSort.Relevance);
    public static readonly ResultView FolderDefault = new(ItemFilter.None, ItemSort.NameAscending);

    public ResultView(ItemFilter filter)
        : this(filter, ItemSort.Relevance)
    {
    }
}
