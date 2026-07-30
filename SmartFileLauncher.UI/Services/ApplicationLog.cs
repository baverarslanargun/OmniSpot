namespace SmartFileLauncher.UI.Services;

public sealed class ApplicationLog
{
    private readonly object _sync = new();
    private readonly List<string> _messages = new();

    public event Action<string>? MessageWritten;

    public void Write(string message)
    {
        ArgumentNullException.ThrowIfNull(message);

        lock (_sync)
        {
            _messages.Add(message);
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
