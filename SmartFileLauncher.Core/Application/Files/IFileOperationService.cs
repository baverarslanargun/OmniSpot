namespace SmartFileLauncher.Core.Application.Files;

public enum FileItemKind
{
    Missing,
    File,
    Directory
}

public sealed record PasteOperationResult(
    string DestinationPath,
    FileItemKind SourceKind);

public sealed record RenameOperationResult(
    string DestinationPath,
    FileItemKind SourceKind);

public interface IFileOperationService
{
    FileItemKind GetItemKind(string path);
    void OpenFile(string path);
    PasteOperationResult Paste(
        string sourcePath,
        string targetFolder,
        bool move);
    RenameOperationResult Rename(string path, string newName);
    FileItemKind DeleteToRecycleBin(string path);
    string CreateFolder(string targetFolder, string name);
    string CreateTextFile(string targetFolder, string name);
    void OpenWith(string path);
    void Reveal(string path);
    void ShowProperties(string path);
}
