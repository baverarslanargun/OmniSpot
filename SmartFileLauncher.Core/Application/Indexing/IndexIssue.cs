using System.ComponentModel;
using SmartFileLauncher.Core.ChangeFeed.Ipc;

namespace SmartFileLauncher.Core.Application.Indexing;

public enum IndexIssueReason
{
    Unknown,
    InvalidLocation,
    LocationUnavailable,
    AccessDenied,
    UnsupportedFileSystem,
    JournalInactive,
    JournalChanging,
    JournalHistoryLost,
    ServiceUnavailable,
    ServiceVersionMismatch,
    ServiceUntrusted,
    StorageFull,
    IndexNotSaved,
    IncompleteScan
}

public sealed record IndexIssue(IndexIssueReason Reason, string? Location, string Operation, string Detail,
    int? NativeErrorCode = null, bool LocationSkipped = false)
{
    internal static IndexIssue FromResponse(ChangeFeedResponse response, string? location, string operation)
    {
        if (response.IndexIssue is { } issue) return issue with { Location = issue.Location ?? location };
        var detail = response.Message ?? "İndeks isteği tamamlanamadı.";
        var reason = response.Status switch
        {
            ChangeFeedResponseStatus.InvalidRequest => IndexIssueReason.InvalidLocation,
            ChangeFeedResponseStatus.RootUnauthorized => IndexIssueReason.AccessDenied,
            ChangeFeedResponseStatus.RootUnusable => IndexIssueReason.LocationUnavailable,
            ChangeFeedResponseStatus.UnsupportedFileSystem => IndexIssueReason.UnsupportedFileSystem,
            ChangeFeedResponseStatus.VersionMismatch => IndexIssueReason.ServiceVersionMismatch,
            _ => IndexIssueReason.Unknown
        };
        return FromLegacyMessage(detail, location, operation, reason);
    }

    public static IndexIssue FromException(Exception failure, string? location, string operation)
    {
        if (failure is IndexIssueException reported) return reported.Issue with { Location = reported.Issue.Location ?? location };
        if (failure is ChangeFeedUntrustedServerException)
            return new(IndexIssueReason.ServiceUntrusted, location, operation, failure.Message);
        if (failure is ChangeFeedPipeException)
            return new(IndexIssueReason.ServiceUnavailable, location, operation, failure.Message);
        if (failure is ArgumentException)
            return new(IndexIssueReason.InvalidLocation, location, operation, failure.Message);
        var nativeCode = failure is Win32Exception win32 ? win32.NativeErrorCode
            : failure.InnerException is Win32Exception inner ? inner.NativeErrorCode
            : (failure.HResult & unchecked((int)0xFFFF0000)) == unchecked((int)0x80070000)
                ? failure.HResult & 0xFFFF : (int?)null;
        return FromLegacyMessage(failure.Message, location, operation, ReasonFor(nativeCode), nativeCode);
    }

    private static IndexIssue FromLegacyMessage(string detail, string? location, string operation,
        IndexIssueReason reason, int? nativeCode = null)
    {
        var separator = detail.IndexOf(" başarısız: ", StringComparison.Ordinal);
        if (separator >= 0)
        {
            var nativeOperation = detail[..separator];
            if (nativeOperation is "FSCTL_QUERY_USN_JOURNAL" or "FSCTL_READ_USN_JOURNAL" &&
                int.TryParse(detail.AsSpan(separator + " başarısız: ".Length), out var code))
                return new(ReasonFor(code), location, nativeOperation, detail, code);
        }
        return new(reason, location, operation, detail, nativeCode);
    }

    private static IndexIssueReason ReasonFor(int? nativeCode) => nativeCode switch
    {
        2 or 3 or 15 or 21 or 53 or 55 or 67 or 1112 or 1167 => IndexIssueReason.LocationUnavailable,
        5 => IndexIssueReason.AccessDenied,
        39 or 112 => IndexIssueReason.StorageFull,
        1178 => IndexIssueReason.JournalChanging,
        1179 => IndexIssueReason.JournalInactive,
        1181 => IndexIssueReason.JournalHistoryLost,
        _ => IndexIssueReason.Unknown
    };
}

public sealed class IndexIssueException(IndexIssue issue, Exception? innerException = null)
    : IOException(issue.Detail, innerException)
{
    public IndexIssue Issue { get; } = issue;
}

public sealed record IndexHealth(IReadOnlyList<IndexIssue> Issues, bool SearchAvailable, bool InitialStartup = false);

public interface IIndexHealthSource
{
    event Action<IndexHealth>? HealthChanged;
}
