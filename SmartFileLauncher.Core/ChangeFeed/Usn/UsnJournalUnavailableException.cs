namespace SmartFileLauncher.Core.ChangeFeed.Usn;

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
