using System.IO;
using System.IO.Pipes;
using System.Runtime.Versioning;
using System.Security.Principal;
using SmartFileLauncher.Core.ChangeFeed.Store;
using SmartFileLauncher.Core.ChangeFeed.Usn;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public readonly record struct ChangeFeedRootDecision(
    ChangeFeedResponseStatus Status,
    string? CanonicalPath,
    ChangeFeedRootIdentity Identity,
    string Diagnostic);

[SupportedOSPlatform("windows")]
public sealed class ChangeFeedRootAdmission
{
    private readonly IUsnIdentityProbe _identityProbe;

    public ChangeFeedRootAdmission(IUsnIdentityProbe identityProbe)
    {
        _identityProbe = identityProbe ?? throw new ArgumentNullException(nameof(identityProbe));
    }

    public (SecurityIdentifier Caller, ChangeFeedRootDecision Decision) Evaluate(
        NamedPipeServerStream pipe,
        string? requestedRoot)
    {
        ArgumentNullException.ThrowIfNull(pipe);

        SecurityIdentifier? caller = null;
        var decision = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid =>
        {
            caller = sid;
            return EvaluateAsCaller(requestedRoot);
        });

        return (caller!, decision);
    }

    private ChangeFeedRootDecision EvaluateAsCaller(string? requestedRoot)
    {
        if (string.IsNullOrWhiteSpace(requestedRoot))
        {
            return Reject(
                ChangeFeedResponseStatus.InvalidRequest,
                "Kök yolu boş olamaz.");
        }

        string canonical;
        try
        {
            canonical = ChangeFeedStoreSecurity.RequireLocalFullyQualifiedPath(requestedRoot);
            ChangeFeedStoreSecurity.RejectRedirectedPath(canonical);
        }
        catch (ChangeFeedStoreSecurityException failure)
        {
            return Reject(ChangeFeedResponseStatus.RootUnusable, failure.Message);
        }

        if (!Directory.Exists(canonical))
        {
            return Reject(
                ChangeFeedResponseStatus.RootUnusable,
                $"Kök bulunamadı: {requestedRoot}");
        }

        try
        {
            using var entries = Directory.EnumerateFileSystemEntries(canonical).GetEnumerator();
            entries.MoveNext();
        }
        catch (UnauthorizedAccessException)
        {
            return Reject(
                ChangeFeedResponseStatus.RootUnauthorized,
                $"Kök listelenemiyor: {requestedRoot}");
        }
        catch (IOException failure)
        {
            return Reject(
                ChangeFeedResponseStatus.RootUnusable,
                $"Kök okunamadı: {requestedRoot} ({failure.GetType().Name})");
        }

        if (!_identityProbe.TryReadIdentity(canonical, out var identity))
        {
            return Reject(
                ChangeFeedResponseStatus.RootUnusable,
                $"Kök kimliği okunamadı: {requestedRoot}");
        }

        return new ChangeFeedRootDecision(
            ChangeFeedResponseStatus.Ok,
            canonical,
            identity.ToChangeFeedRootIdentity(),
            string.Empty);
    }

    private static ChangeFeedRootDecision Reject(ChangeFeedResponseStatus status, string diagnostic) =>
        new(status, null, ChangeFeedRootIdentity.Unknown, diagnostic);
}
