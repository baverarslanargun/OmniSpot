namespace SmartFileLauncher.Core.Application.Indexing;

public sealed record IndexLocations(
    string DesktopPath,
    IReadOnlyList<string> RootPaths);
