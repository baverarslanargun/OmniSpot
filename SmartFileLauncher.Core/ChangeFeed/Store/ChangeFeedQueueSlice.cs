namespace SmartFileLauncher.Core.ChangeFeed.Store;

public sealed class ChangeFeedReadBudget
{
    public const int DefaultMaximumEntries = 64;

    public const long DefaultMaximumBytes = 512L * 1024;

    public ChangeFeedReadBudget(int maximumEntries, long maximumBytes)
    {
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumEntries);
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(maximumBytes);

        MaximumEntries = maximumEntries;
        MaximumBytes = maximumBytes;
    }

    public static ChangeFeedReadBudget Default { get; } =
        new(DefaultMaximumEntries, DefaultMaximumBytes);

    public int MaximumEntries { get; }

    public long MaximumBytes { get; }
}

public sealed class ChangeFeedQueueSlice
{
    public ChangeFeedQueueSlice(IReadOnlyList<ChangeFeedQueueEntry> entries, bool hasMore)
    {
        Entries = entries ?? throw new ArgumentNullException(nameof(entries));
        HasMore = hasMore;
    }

    public IReadOnlyList<ChangeFeedQueueEntry> Entries { get; }

    public bool HasMore { get; }
}
