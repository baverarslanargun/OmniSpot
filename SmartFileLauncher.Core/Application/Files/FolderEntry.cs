namespace SmartFileLauncher.Core.Application.Files;

public sealed record FolderEntry(
    string Name,
    string FullPath,
    bool IsDirectory);

public sealed record FolderPage(
    IReadOnlyList<FolderEntry> Entries,
    bool IsTruncated);
