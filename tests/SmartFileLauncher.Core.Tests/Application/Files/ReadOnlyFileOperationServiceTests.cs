using SmartFileLauncher.Core.Application.Files;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Files;

public sealed class ReadOnlyFileOperationServiceTests
{
    [Fact]
    public void MutatingOperationsFailBeforeInnerServiceRuns()
    {
        var inner = new RecordingFileOperationService();
        var service = new ReadOnlyFileOperationService(inner);

        Assert.Throws<UnauthorizedAccessException>(() => service.Paste("source", "target", false));
        Assert.Throws<UnauthorizedAccessException>(() => service.Rename("path", "new-name"));
        Assert.Throws<UnauthorizedAccessException>(() => service.DeleteToRecycleBin("path"));
        Assert.Throws<UnauthorizedAccessException>(() => service.CreateFolder("target", "new-folder"));
        Assert.Throws<UnauthorizedAccessException>(() => service.CreateTextFile("target", "new-file"));
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public void ReadAndOpenOperationsAreBlockedBeforeInnerServiceRuns()
    {
        var inner = new RecordingFileOperationService();
        var service = new ReadOnlyFileOperationService(inner);

        Assert.Equal(FileItemKind.File, service.GetItemKind("path"));
        Assert.Throws<UnauthorizedAccessException>(() => service.OpenFile("path"));
        Assert.Throws<UnauthorizedAccessException>(() => service.OpenWith("path"));
        Assert.Throws<UnauthorizedAccessException>(() => service.Reveal("path"));
        Assert.Throws<UnauthorizedAccessException>(() => service.ShowProperties("path"));

        Assert.Equal(1, inner.CallCount);
    }

    private sealed class RecordingFileOperationService : IFileOperationService
    {
        public int CallCount { get; private set; }

        public FileItemKind GetItemKind(string path)
        {
            CallCount++;
            return FileItemKind.File;
        }

        public void OpenFile(string path) => CallCount++;

        public PasteOperationResult Paste(string sourcePath, string targetFolder, bool move)
        {
            CallCount++;
            return new PasteOperationResult(targetFolder, FileItemKind.File);
        }

        public RenameOperationResult Rename(string path, string newName)
        {
            CallCount++;
            return new RenameOperationResult(newName, FileItemKind.File);
        }

        public FileItemKind DeleteToRecycleBin(string path)
        {
            CallCount++;
            return FileItemKind.File;
        }

        public string CreateFolder(string targetFolder, string name)
        {
            CallCount++;
            return name;
        }

        public string CreateTextFile(string targetFolder, string name)
        {
            CallCount++;
            return name;
        }

        public void OpenWith(string path) => CallCount++;

        public void Reveal(string path) => CallCount++;

        public void ShowProperties(string path) => CallCount++;
    }
}
