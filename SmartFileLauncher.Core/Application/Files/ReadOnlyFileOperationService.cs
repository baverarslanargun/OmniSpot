namespace SmartFileLauncher.Core.Application.Files;

public sealed class ReadOnlyFileOperationService : IFileOperationService
{
    private readonly IFileOperationService _inner;

    public ReadOnlyFileOperationService(IFileOperationService inner)
    {
        _inner = inner ?? throw new ArgumentNullException(nameof(inner));
    }

    public FileItemKind GetItemKind(string path) => _inner.GetItemKind(path);

    public void OpenFile(string path) => throw CreateDisabledException();

    public PasteOperationResult Paste(
        string sourcePath,
        string targetFolder,
        bool move)
    {
        throw CreateDisabledException();
    }

    public RenameOperationResult Rename(string path, string newName)
    {
        throw CreateDisabledException();
    }

    public FileItemKind DeleteToRecycleBin(string path)
    {
        throw CreateDisabledException();
    }

    public string CreateFolder(string targetFolder, string name)
    {
        throw CreateDisabledException();
    }

    public string CreateTextFile(string targetFolder, string name)
    {
        throw CreateDisabledException();
    }

    public void OpenWith(string path) => throw CreateDisabledException();

    public void Reveal(string path) => throw CreateDisabledException();

    public void ShowProperties(string path) => throw CreateDisabledException();

    private static UnauthorizedAccessException CreateDisabledException()
    {
        return new UnauthorizedAccessException(
            "uretim-kopya profilinde kullanıcı dosyalarını değiştiren işlemler devre dışıdır.");
    }
}
