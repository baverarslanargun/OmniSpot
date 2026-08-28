namespace SmartFileLauncher.UI.Services;

public sealed class ApplicationLog
{
    public const int MaxRetainedMessages = 5000;
    private readonly object _sync = new();
    private readonly Queue<string> _messages = new();
    private readonly bool _redactPaths;

    public ApplicationLog(bool redactPaths = false)
    {
        _redactPaths = redactPaths;
    }

    public event Action<string>? MessageWritten;

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        if (_redactPaths)
        {
            message = DiagnosticPathRedactor.Redact(message);
        }

        message = $"[{DateTime.Now:HH:mm:ss.fff}] {message}";

        lock (_sync)
        {
            _messages.Enqueue(message);
            while (_messages.Count > MaxRetainedMessages)
            {
                _messages.Dequeue();
            }
        }

        MessageWritten?.Invoke(message);
    }

    public IReadOnlyList<string> GetSnapshot()
    {
        lock (_sync)
        {
            return _messages.ToArray();
        }
    }
}
