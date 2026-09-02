namespace SmartFileLauncher.Core.ChangeFeed.Store;

public sealed class ChangeFeedStoreSecurityException : Exception
{
    public ChangeFeedStoreSecurityException(string message)
        : base(message)
    {
    }

    public ChangeFeedStoreSecurityException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
