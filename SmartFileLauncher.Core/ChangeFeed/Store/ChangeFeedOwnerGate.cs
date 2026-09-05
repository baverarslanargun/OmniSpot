using System.Collections.Concurrent;

namespace SmartFileLauncher.Core.ChangeFeed.Store;

public static class ChangeFeedOwnerGate
{
    private static readonly ConcurrentDictionary<string, object> Gates =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TimeSpan PollInterval = TimeSpan.FromMilliseconds(25);

    public static IDisposable Enter(
        string ownerKey,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(ownerKey);

        var gate = Gates.GetOrAdd(ownerKey, _ => new object());
        return new Scope(gate, cancellationToken);
    }

    public static bool IsHeld(string ownerKey) =>
        Gates.TryGetValue(ownerKey, out var gate) && Monitor.IsEntered(gate);

    private sealed class Scope : IDisposable
    {
        private readonly object _gate;
        private bool _released;

        public Scope(object gate, CancellationToken cancellationToken)
        {
            _gate = gate;

            while (!Monitor.TryEnter(gate, PollInterval))
            {
                cancellationToken.ThrowIfCancellationRequested();
            }
        }

        public void Dispose()
        {
            if (_released)
            {
                return;
            }

            _released = true;
            Monitor.Exit(_gate);
        }
    }
}
