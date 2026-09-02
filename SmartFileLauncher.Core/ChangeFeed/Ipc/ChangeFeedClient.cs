using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedClient
{
    private readonly string _pipeName;
    private readonly IReadOnlySet<SecurityIdentifier> _trustedServerOwners;
    private readonly TimeSpan _busyWait;

    public ChangeFeedClient(
        string pipeName = ChangeFeedProtocol.PipeName,
        IReadOnlySet<SecurityIdentifier>? trustedServerOwners = null,
        TimeSpan? busyWait = null)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(pipeName);
        _pipeName = pipeName;
        _trustedServerOwners = trustedServerOwners ?? DefaultTrustedOwners();
        _busyWait = busyWait ?? DefaultBusyWait;
    }

    public static TimeSpan DefaultBusyWait => ChangeFeedProtocol.IoTimeout;

    public TimeSpan BusyWait => _busyWait;

    public static IReadOnlySet<SecurityIdentifier> DefaultTrustedOwners() =>
        new HashSet<SecurityIdentifier>
        {
            new(WellKnownSidType.LocalSystemSid, null)
        };

    public async Task<ChangeFeedResponse> SendAsync(
        ChangeFeedRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);

        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(ChangeFeedProtocol.IoTimeout);

        try
        {
            using var pipe = ChangeFeedPipeFactory.Connect(
                _pipeName,
                TokenImpersonationLevel.Impersonation,
                busyWait: _busyWait,
                cancellationToken: deadline.Token);

            VerifyServerOwner(pipe);

            await ChangeFeedMessageChannel
                .WriteAsync(pipe, request, deadline.Token)
                .ConfigureAwait(false);

            return await ChangeFeedMessageChannel
                .ReadAsync<ChangeFeedResponse>(pipe, deadline.Token)
                .ConfigureAwait(false);
        }
        catch (ChangeFeedUntrustedServerException)
        {
            throw;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (OperationCanceledException)
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.Unavailable,
                $"{_pipeName} adlı kanal ayrılan süre içinde yanıt vermedi.");
        }
        catch (ChangeFeedPipeException failure)
        {
            return ChangeFeedResponse.Failed(
                ChangeFeedResponseStatus.Unavailable,
                failure.Message);
        }
    }

    private void VerifyServerOwner(NamedPipeClientStream pipe)
    {
        SecurityIdentifier? owner;
        try
        {
            owner = pipe.GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier;
        }
        catch (Exception failure)
            when (failure is UnauthorizedAccessException or PrivilegeNotHeldException or IOException)
        {
            throw new ChangeFeedUntrustedServerException(
                "Kanal sahibi okunamadı; sunucu doğrulanamıyor.",
                failure);
        }

        if (owner is null || !_trustedServerOwners.Contains(owner))
        {
            throw new ChangeFeedUntrustedServerException(
                $"Kanal sahibi güvenilir değil: {owner?.ToString() ?? "(bilinmiyor)"}");
        }
    }
}
