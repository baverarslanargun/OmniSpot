using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.IO;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Files;

public sealed class FolderBrowserServiceTests
{
    [Fact]
    public async Task LoadReturnsDirectoriesBeforeFilesAndSortsEachGroup()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateDirectory(Path.Combine("root", "z-folder"));
        workspace.CreateDirectory(Path.Combine("root", "a-folder"));
        workspace.CreateFile(Path.Combine("root", "z-file.txt"));
        workspace.CreateFile(Path.Combine("root", "a-file.txt"));
        var service = new FolderBrowserService();

        var page = await service.LoadAsync(root, 100);

        Assert.Equal(
            new[] { "a-folder", "z-folder", "a-file.txt", "z-file.txt" },
            page.Entries.Select(entry => entry.Name));
        Assert.Equal(
            new[] { true, true, false, false },
            page.Entries.Select(entry => entry.IsDirectory));
        Assert.False(page.IsTruncated);
    }

    [Fact]
    public async Task LoadHonorsLimit()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        for (var index = 0; index < 5; index++)
        {
            workspace.CreateFile(Path.Combine("root", $"file-{index}.txt"));
        }

        var page = await new FolderBrowserService().LoadAsync(root, 2);

        Assert.Equal(2, page.Entries.Count);
        Assert.True(page.IsTruncated);
    }

    [Fact]
    public async Task LoadObservesCancellation()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            new FolderBrowserService().LoadAsync(root, 100, cancellation.Token));
    }

    [Fact]
    public async Task MeasurementModeSkipsEntriesMarkedAsReparsePoints()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var skippedDirectory = workspace.CreateDirectory(Path.Combine("root", "linked"));
        workspace.CreateFile(Path.Combine("root", "visible.txt"));
        var guard = new FileSystemPathGuard(
            path =>
            {
                var attributes = ReadAttributes(path);
                return attributes.HasValue && path.Equals(
                    skippedDirectory,
                    StringComparison.OrdinalIgnoreCase)
                    ? attributes.Value | FileAttributes.ReparsePoint
                    : attributes;
            },
            path => Directory.GetFileSystemEntries(path),
            path => path);
        var service = new FolderBrowserService(
            skipReparsePoints: true,
            pathGuard: guard);

        var page = await service.LoadAsync(root, 100);

        Assert.DoesNotContain(page.Entries, entry => entry.Name == "linked");
        Assert.Contains(page.Entries, entry => entry.Name == "visible.txt");
    }

    [Fact]
    public async Task MeasurementModeSkipsRealJunctionTarget()
    {
        if (!OperatingSystem.IsWindows()) return;

        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var target = workspace.CreateDirectory("outside");
        var link = Path.Combine(root, "linked");
        workspace.CreateFile(Path.Combine("outside", "sentinel.txt"));
        workspace.CreateFile(Path.Combine("root", "visible.txt"));
        WindowsDirectoryLink.CreateJunction(link, target);

        try
        {
            var page = await new FolderBrowserService(skipReparsePoints: true)
                .LoadAsync(root, 100);

            Assert.DoesNotContain(page.Entries, entry => entry.Name == "linked");
            Assert.Contains(page.Entries, entry => entry.Name == "visible.txt");
            Assert.DoesNotContain(page.Entries, entry => entry.Name == "sentinel.txt");
        }
        finally
        {
            WindowsDirectoryLink.Delete(link);
        }
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
}
