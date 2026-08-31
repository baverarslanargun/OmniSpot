namespace SmartFileLauncher.Core.ChangeFeed.Usn;

public sealed class UsnProtocolRejectedException : Exception
{
    public UsnProtocolRejectedException(string message, int errorCode)
        : base(message)
    {
        ErrorCode = errorCode;
    }

    public UsnProtocolRejectedException(string message, int errorCode, Exception innerException)
        : base(message, innerException)
    {
        ErrorCode = errorCode;
    }

    public int ErrorCode { get; }
}
