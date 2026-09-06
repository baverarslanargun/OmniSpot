namespace SmartFileLauncher.Core.ChangeFeed;

public readonly record struct ChangeFeedSecurityStamp(string Value)
{
    public static readonly ChangeFeedSecurityStamp Unknown = new(string.Empty);

    public bool IsUnknown => string.IsNullOrWhiteSpace(Value);

    public static ChangeFeedSecurityStamp New() => new(Guid.NewGuid().ToString("N"));

    public bool Matches(ChangeFeedSecurityStamp other) =>
        !IsUnknown &&
        !other.IsUnknown &&
        string.Equals(Value, other.Value, StringComparison.Ordinal);

    public override string ToString() => IsUnknown ? "(bilinmiyor)" : Value;
}
