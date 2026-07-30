using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.Core.Application.Indexing;

public sealed record IndexStartupResult(
    string DesktopPath,
    IReadOnlyList<string> RootPaths,
    IndexStats Stats);

public sealed record IndexReconciliationStatus(
    bool IsRunning,
    int Progress,
    int Processed,
    int Total);

public sealed record IndexTokenMatches(
    int Count,
    IReadOnlyList<string> SampleNames);
