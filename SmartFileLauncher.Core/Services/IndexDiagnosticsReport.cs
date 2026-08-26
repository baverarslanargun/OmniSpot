namespace SmartFileLauncher.Core.Services;

public sealed record IndexDiagnosticsReport(
    long ReconciliationRuns,
    DateTime? LastReconciliationAt,
    TimeSpan LastReconciliationDuration,
    TimeSpan LastReconciliationScanDuration,
    int LastReconciliationChanges,
    bool RepublishedDuringLastReconciliation,
    long RepublishCount,
    DateTime? LastRepublishAt,
    TimeSpan LastRepublishDuration,
    int SearchStateItemCount);
