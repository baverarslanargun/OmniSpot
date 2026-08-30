namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed class UsnRecordFormatException : Exception
{
    public UsnRecordFormatException(string message)
        : base(message)
    {
    }
}
