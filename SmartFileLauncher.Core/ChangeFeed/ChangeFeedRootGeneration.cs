namespace SmartFileLauncher.Core.ChangeFeed;

public readonly record struct ChangeFeedRootGeneration(string Value)
{
    public static readonly ChangeFeedRootGeneration Unknown = new(string.Empty);

    public bool IsUnknown => string.IsNullOrWhiteSpace(Value);

    public static ChangeFeedRootGeneration New() => new(Guid.NewGuid().ToString("N"));

    public bool Matches(ChangeFeedRootGeneration other) =>
        !IsUnknown &&
        !other.IsUnknown &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override string ToString() => IsUnknown ? "(bilinmiyor)" : Value;
}
