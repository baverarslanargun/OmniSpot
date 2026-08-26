namespace SmartFileLauncher.UI.Services;

public sealed class ApplicationLog
{
    public const int MaxRetainedMessages = 5000;

    private readonly object _sync = new();
    private readonly Queue<string> _messages = new();

    public event Action<string>? MessageWritten;

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

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
