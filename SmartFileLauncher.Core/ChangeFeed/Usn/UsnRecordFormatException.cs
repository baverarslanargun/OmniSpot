namespace SmartFileLauncher.Core.ChangeFeed.Usn;

/// <summary>
/// Raised when a journal buffer cannot be parsed. The feed turns this into a
/// gap instead of silently dropping records.
/// </summary>
public sealed class UsnRecordFormatException : Exception
{
    public UsnRecordFormatException(string message)
        : base(message)
    {
    }
}
