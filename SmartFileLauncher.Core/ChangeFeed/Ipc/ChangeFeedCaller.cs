using System.IO.Pipes;
using System.Security.Principal;
using System.Runtime.Versioning;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
internal sealed class ChangeFeedCaller : IDisposable
{
    private readonly NamedPipeServerStream? _pipe;
    private readonly WindowsIdentity? _identity;
    private readonly object _gate = new();
    private bool _disposed;

    private ChangeFeedCaller(NamedPipeServerStream pipe) => _pipe = pipe;
    private ChangeFeedCaller(WindowsIdentity identity) => _identity = identity;
    internal static ChangeFeedCaller FromPipe(NamedPipeServerStream pipe)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        return new(pipe);
    }

    internal ChangeFeedCaller Capture() => Run(_ => new ChangeFeedCaller(
        WindowsIdentity.GetCurrent(TokenAccessLevels.Query | TokenAccessLevels.Duplicate | TokenAccessLevels.Impersonate)));

    internal T Run<T>(Func<SecurityIdentifier, T> action)
    {
        lock (_gate)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);
            if (_pipe is not null) return ChangeFeedCallerIdentity.RunAsVerifiedCaller(_pipe, action);
            ChangeFeedCallerIdentity.EnsureNotImpersonating();
            try
            {
                return WindowsIdentity.RunImpersonated(_identity!.AccessToken, () =>
                {
                    using var current = WindowsIdentity.GetCurrent();
                    if (current.User is not { } sid || _identity.User is not { } expected || !sid.Equals(expected) ||
                        current.ImpersonationLevel is not (TokenImpersonationLevel.Impersonation or TokenImpersonationLevel.Delegation))
                        throw new ChangeFeedImpersonationException("Katalog sahibinin token kimliği doğrulanamadı.");
                    return action(sid);
                });
            }
            finally { ChangeFeedCallerIdentity.EnsureNotImpersonating(); }
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _identity?.Dispose();
        }
    }
}
