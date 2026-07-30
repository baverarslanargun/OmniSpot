using SmartFileLauncher.Core.Application.Files;
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
}
