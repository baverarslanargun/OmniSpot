namespace SmartFileLauncher.Core.ChangeFeed;

/// <summary>
/// Provider-opaque identity of a feed root. Only compared for equality; the
/// values are never parsed by consumers.
/// </summary>
public readonly record struct ChangeFeedRootIdentity(string VolumeId, string NodeId)
{
    public static readonly ChangeFeedRootIdentity Unknown = new(string.Empty, string.Empty);

    public bool IsUnknown =>
        string.IsNullOrEmpty(VolumeId) && string.IsNullOrEmpty(NodeId);

    public override string ToString() => IsUnknown ? "(bilinmiyor)" : $"{VolumeId}/{NodeId}";
}
