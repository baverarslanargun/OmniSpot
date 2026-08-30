namespace SmartFileLauncher.Core.ChangeFeed;

public readonly record struct ChangeFeedRootIdentity(string VolumeId, string NodeId)
{
    public static readonly ChangeFeedRootIdentity Unknown = new(string.Empty, string.Empty);

    public bool IsUnknown =>
        string.IsNullOrEmpty(VolumeId) && string.IsNullOrEmpty(NodeId);

    public override string ToString() => IsUnknown ? "(bilinmiyor)" : $"{VolumeId}/{NodeId}";
}
