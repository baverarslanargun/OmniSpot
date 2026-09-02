using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed class ChangeFeedImpersonationException : Exception
{
    public ChangeFeedImpersonationException(string message)
        : base(message)
    {
    }

    public ChangeFeedImpersonationException(string message, Exception innerException)
        : base(message, innerException)
    {
    }
}

[SupportedOSPlatform("windows")]
public static class ChangeFeedCallerIdentity
{
    public static TResult RunAsVerifiedCaller<TResult>(
        NamedPipeServerStream pipe,
        Func<SecurityIdentifier, TResult> work)
    {
        ArgumentNullException.ThrowIfNull(pipe);
        ArgumentNullException.ThrowIfNull(work);

        var privilegedSid = CurrentUserSid();

        var completed = false;
        TResult? result = default;
        Exception? failure = null;

        try
        {
            pipe.RunAsClient(() =>
            {
                try
                {
                    using var caller = WindowsIdentity.GetCurrent();

                    if (caller.ImpersonationLevel != TokenImpersonationLevel.Impersonation &&
                        caller.ImpersonationLevel != TokenImpersonationLevel.Delegation)
                    {
                        failure = new ChangeFeedImpersonationException(
                            $"Çağıranın bürünme seviyesi yetersiz: {caller.ImpersonationLevel}");
                        return;
                    }

                    if (caller.User is not { } callerSid)
                    {
                        failure = new ChangeFeedImpersonationException(
                            "Çağıranın kimliği token'dan okunamadı.");
                        return;
                    }

                    result = work(callerSid);
                    completed = true;
                }
                catch (Exception inner)
                {
                    failure = inner;
                }
            });
        }
        catch (Exception outer)
        {
            throw new ChangeFeedImpersonationException(
                "Çağıranın kimliğine bürünülemedi.",
                outer);
        }

        VerifyReverted(privilegedSid);

        if (failure is not null)
        {
            throw failure is ChangeFeedImpersonationException
                ? failure
                : new ChangeFeedImpersonationException(
                    "Bürünmüş bağlamda iş başarısız oldu.",
                    failure);
        }

        if (!completed)
        {
            throw new ChangeFeedImpersonationException("Bürünmüş bağlamda iş tamamlanmadı.");
        }

        return result!;
    }

    public static void EnsureNotImpersonating()
    {
        var impersonated = WindowsIdentity.GetCurrent(ifImpersonating: true);

        if (impersonated is null)
        {
            return;
        }

        using (impersonated)
        {
            throw new ChangeFeedImpersonationException(
                $"Thread hâlâ bürünme token'ı taşıyor: seviye {impersonated.ImpersonationLevel}");
        }
    }

    private static void VerifyReverted(SecurityIdentifier privilegedSid)
    {
        EnsureNotImpersonating();

        var current = CurrentUserSid();

        if (!current.Equals(privilegedSid))
        {
            throw new ChangeFeedImpersonationException(
                $"Bürünme sonrası ayrıcalıklı bağlama dönülemedi: {current} != {privilegedSid}");
        }
    }

    private static SecurityIdentifier CurrentUserSid()
    {
        using var identity = WindowsIdentity.GetCurrent();
        return identity.User
            ?? throw new ChangeFeedImpersonationException("Mevcut kimlik okunamadı.");
    }
}
