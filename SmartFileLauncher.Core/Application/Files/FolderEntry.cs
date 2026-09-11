namespace SmartFileLauncher.Core.Application.Files;

public sealed record FolderEntry(
    string Name,
    string FullPath,
    bool IsDirectory,
    long? SizeBytes = null,
    DateTime? LastWriteTime = null);

public sealed record FolderPage(
    IReadOnlyList<FolderEntry> Entries,
    bool IsTruncated);
