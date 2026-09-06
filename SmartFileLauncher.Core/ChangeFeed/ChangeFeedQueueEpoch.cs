namespace SmartFileLauncher.Core.ChangeFeed;

public readonly record struct ChangeFeedQueueEpoch(string Value)
{
    public static readonly ChangeFeedQueueEpoch Unknown = new(string.Empty);

    public bool IsUnknown => string.IsNullOrWhiteSpace(Value);

    public static ChangeFeedQueueEpoch New() => new(Guid.NewGuid().ToString("N"));

    public bool Matches(ChangeFeedQueueEpoch other) =>
        !IsUnknown &&
        !other.IsUnknown &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override string ToString() => IsUnknown ? "(bilinmiyor)" : Value;
}
