using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.IO;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Files;

public sealed class RootScopedFileOperationServiceTests
{
    [Fact]
    public void OutsidePathsAreRejectedBeforeInnerServiceRuns()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("corpus");
        var inside = workspace.CreateFile(Path.Combine("corpus", "inside.txt"));
        var outside = workspace.CreateFile("outside.txt");
        var inner = new RecordingFileOperationService();
        var service = new RootScopedFileOperationService(inner, root);

        Assert.Equal(FileItemKind.Missing, service.GetItemKind(outside));
        Assert.Throws<UnauthorizedAccessException>(() => service.OpenFile(outside));
        Assert.Throws<UnauthorizedAccessException>(() => service.Paste(outside, root, false));
        Assert.Throws<UnauthorizedAccessException>(() => service.Paste(inside, workspace.Path, false));
        Assert.Throws<UnauthorizedAccessException>(() => service.Rename(inside, @"..\outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => service.DeleteToRecycleBin(outside));
        Assert.Throws<UnauthorizedAccessException>(() => service.CreateFolder(root, @"..\outside"));
        Assert.Throws<UnauthorizedAccessException>(() => service.CreateTextFile(root, @"..\outside.txt"));
        Assert.Throws<UnauthorizedAccessException>(() => service.OpenWith(outside));
        Assert.Throws<UnauthorizedAccessException>(() => service.Reveal(outside));
        Assert.Throws<UnauthorizedAccessException>(() => service.ShowProperties(outside));
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public void InsidePathsAreForwardedToInnerService()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("corpus");
        var inside = workspace.CreateFile(Path.Combine("corpus", "inside.txt"));
        var inner = new RecordingFileOperationService();
        var service = new RootScopedFileOperationService(inner, root);

        Assert.Equal(FileItemKind.File, service.GetItemKind(inside));
        service.OpenFile(inside);
        service.OpenWith(inside);
        service.Reveal(inside);
        service.ShowProperties(inside);

        Assert.Equal(5, inner.CallCount);
    }

    [Fact]
    public void PhysicalAliasResolvingOutsideRootIsRejected()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("corpus");
        var alias = workspace.CreateFile(Path.Combine("corpus", "alias.txt"));
        var outside = workspace.CreateFile("outside.txt");
        var inner = new RecordingFileOperationService();
        var guard = CreateGuard(
            resolveExistingPath: path => path.Equals(
                alias,
                StringComparison.OrdinalIgnoreCase)
                ? outside
                : path);
        var service = new RootScopedFileOperationService(inner, root, guard);

        Assert.Equal(FileItemKind.Missing, service.GetItemKind(alias));
        Assert.Throws<UnauthorizedAccessException>(() => service.OpenFile(alias));
        Assert.Equal(0, inner.CallCount);
    }

    [Fact]
    public void PasteRejectsReparsePointInsideSourceTree()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("corpus");
        var source = workspace.CreateDirectory(Path.Combine("corpus", "source"));
        var nested = workspace.CreateDirectory(Path.Combine("corpus", "source", "nested"));
        var target = workspace.CreateDirectory(Path.Combine("corpus", "target"));
        var inner = new RecordingFileOperationService();
        var guard = CreateGuard(path =>
        {
            var attributes = ReadAttributes(path);
            return attributes.HasValue && path.Equals(
                nested,
                StringComparison.OrdinalIgnoreCase)
                ? attributes.Value | FileAttributes.ReparsePoint
                : attributes;
        });
        var service = new RootScopedFileOperationService(inner, root, guard);

        Assert.Throws<UnauthorizedAccessException>(() =>
            service.Paste(source, target, move: false));
        Assert.Equal(0, inner.CallCount);
    }

    private static FileSystemPathGuard CreateGuard(
        Func<string, FileAttributes?>? readAttributes = null,
        Func<string, string>? resolveExistingPath = null)
    {
        return new FileSystemPathGuard(
            readAttributes ?? ReadAttributes,
            path => Directory.GetFileSystemEntries(path),
            resolveExistingPath ?? (path => path));
    }

    private static FileAttributes? ReadAttributes(string path)
    {
        try
        {
            return File.GetAttributes(path);
        }
        catch (FileNotFoundException)
        {
            return null;
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }
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
            return new PasteOperationResult(Path.Combine(targetFolder, Path.GetFileName(sourcePath)), FileItemKind.File);
        }

        public RenameOperationResult Rename(string path, string newName)
        {
            CallCount++;
            return new RenameOperationResult(
                Path.Combine(Path.GetDirectoryName(path)!, newName),
                FileItemKind.File);
        }

        public FileItemKind DeleteToRecycleBin(string path)
        {
            CallCount++;
            return FileItemKind.File;
        }

        public string CreateFolder(string targetFolder, string name)
        {
            CallCount++;
            return Path.Combine(targetFolder, name);
        }

        public string CreateTextFile(string targetFolder, string name)
        {
            CallCount++;
            return Path.Combine(targetFolder, name);
        }

        public void OpenWith(string path) => CallCount++;

        public void Reveal(string path) => CallCount++;

        public void ShowProperties(string path) => CallCount++;
    }
}
