namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// Raised when the change journal cannot be queried or read. The feed turns
/// this into <see cref="ChangeFeedGapReason.JournalUnavailable"/>.
/// </summary>
public sealed class UsnJournalUnavailableException : Exception
{
    public UsnJournalUnavailableException(string message)
        : base(message)
    {
    }

    public UsnJournalUnavailableException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}
