using System.IO.Pipes;

namespace SmartFileLauncher.Core.ChangeFeed.Ipc;

public sealed partial class ChangeFeedAdmissionService
{
    private ChangeFeedResponse PrepareContinuousRoot(NamedPipeServerStream pipe, string? path, CancellationToken ct)
    {
        if (path is not null && Path.IsPathFullyQualified(path) && path.StartsWith(@"\\", StringComparison.Ordinal))
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.UnsupportedFileSystem, "Ağ konumunda USN desteklenmiyor.");
        var (_, decision) = _admission.Evaluate(pipe, path);
        if (decision.Status != ChangeFeedResponseStatus.Ok)
            return ChangeFeedResponse.Failed(decision.Status, decision.Diagnostic);
        try
        {
            var drive = new DriveInfo(Path.GetPathRoot(decision.CanonicalPath!)!);
            if (!drive.DriveFormat.Equals("NTFS", StringComparison.OrdinalIgnoreCase))
                return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.UnsupportedFileSystem, "Bu dosya sisteminde USN desteklenmiyor.");
        }
        catch (IOException failure) { return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, failure.Message); }
        return AddRoot(pipe, decision.CanonicalPath, ct);
    }

    private ChangeFeedResponse DrainContinuous(NamedPipeServerStream pipe, CancellationToken ct)
    {
        var caller = ChangeFeedCallerIdentity.RunAsVerifiedCaller(pipe, sid => sid);
        var store = _storeFactory(caller.Value);
        using var owner = store.EnterOwnerScope(ct);
        store.ReleaseLease();
        if (_handoffDrainer is null)
            return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, "USN boşaltıcısı hazır değil.");
        using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
        budget.CancelAfter(_handoffDrainBudget);
        try
        {
            return _handoffDrainer(caller.Value, budget.Token)
                ? ChangeFeedResponse.Ok()
                : ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, "USN boşaltması sürecek.");
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception failure)
        { return ChangeFeedResponse.Failed(ChangeFeedResponseStatus.Unavailable, "USN boşaltması tamamlanamadı: " + failure.Message); }
    }
}
