namespace SmartFileLauncher.Core.Application.Refresh;

public sealed class RefreshCoalescer
{
    private int _pending;
    private int _running;

    public void Request()
    {
        Interlocked.Exchange(ref _pending, 1);
    }

    public bool TryBegin()
    {
        if (Interlocked.CompareExchange(ref _running, 1, 0) != 0)
        {
            return false;
        }

        if (Interlocked.Exchange(ref _pending, 0) != 0)
        {
            return true;
        }

        Volatile.Write(ref _running, 0);
        return false;
    }

    public bool Complete()
    {
        Volatile.Write(ref _running, 0);
        return Volatile.Read(ref _pending) != 0;
    }
}
