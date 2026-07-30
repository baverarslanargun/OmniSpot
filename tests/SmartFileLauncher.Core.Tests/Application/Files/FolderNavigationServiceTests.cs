using SmartFileLauncher.Core.Application.Files;
using SmartFileLauncher.Core.Application.Indexing;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Files;

public sealed class FolderNavigationServiceTests
{
    [Fact]
    public async Task OpenAsync_SynchronizesBeforeLoadingWhenRequired()
    {
        var calls = new List<string>();
        var browser = new RecordingFolderBrowser(calls);
        var service = new FolderNavigationService(
            browser,
            () => new IndexReconciliationStatus(true, 0, 0, 1),
            (path, _) => {
                calls.Add($"sync:{path}");
                return Task.FromResult(true);
            });

        await service.OpenAsync(
            @"C:\Root\Child",
            100,
            ensureSynchronized: true);

        Assert.Equal(
            [@"sync:C:\Root\Child", @"load:C:\Root\Child"],
            calls);
    }

    [Fact]
    public void GetParentWithinRoots_ReturnsParentForNestedPath()
    {
        var service = CreateService();

        var parent = service.GetParentWithinRoots(
            @"C:\Root\Parent\Child",
            [@"C:\Root"]);

        Assert.Equal(@"C:\Root\Parent", parent);
    }

    [Fact]
    public void GetParentWithinRoots_ReturnsHomeForRoot()
    {
        var service = CreateService();

        var parent = service.GetParentWithinRoots(
            @"C:\Root",
            [@"C:\Root"]);

        Assert.Null(parent);
    }

    [Fact]
    public void GetParentWithinRoots_RejectsSiblingWithSamePrefix()
    {
        var service = CreateService();

        var parent = service.GetParentWithinRoots(
            @"C:\Root2\Child",
            [@"C:\Root"]);

        Assert.Null(parent);
    }

    private static FolderNavigationService CreateService()
    {
        return new FolderNavigationService(
            new RecordingFolderBrowser([]),
            () => new IndexReconciliationStatus(false, 0, 0, 0),
            (_, _) => Task.FromResult(true));
    }

    private sealed class RecordingFolderBrowser(
        List<string> calls) : IFolderBrowserService
    {
        public Task<FolderPage> LoadAsync(
            string folderPath,
            int limit,
            CancellationToken cancellationToken = default)
        {
            calls.Add($"load:{folderPath}");
            return Task.FromResult(
                new FolderPage(Array.Empty<FolderEntry>(), false));
        }
    }
}
