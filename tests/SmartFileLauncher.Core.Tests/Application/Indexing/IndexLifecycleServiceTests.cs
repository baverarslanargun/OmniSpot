using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class IndexLifecycleServiceTests
{
    [Fact]
    public async Task InitializeReturnsResolvedLocationsAndSearchableRootEntries()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(Path.Combine("root", "document.txt"));
        var manager = new IndexManager(
            new IndexDatabase(Path.Combine(workspace.Path, "index.db")),
            new FileWatcherService(debounceMs: 1));
        using var service = new IndexLifecycleService(
            manager,
            new StaticLocationProvider(root));

        var result = await service.InitializeAsync();

        Assert.True(service.IsInitialized);
        Assert.Equal(root, result.DesktopPath);
        Assert.Equal(new[] { root }, result.RootPaths);
        Assert.Equal(1, result.Stats.FileCount);
        Assert.Contains(
            service.GetIndexedRoots().Single().Children,
            entry => string.Equals(
                entry.FullPath,
                Path.Combine(root, "document.txt"),
                StringComparison.OrdinalIgnoreCase));
        Assert.Equal(1, service.GetTokenMatches("document").Count);
    }

    [Fact]
    public async Task DisposeRejectsFurtherOperations()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var manager = new IndexManager(
            new IndexDatabase(Path.Combine(workspace.Path, "index.db")),
            new FileWatcherService(debounceMs: 1));
        var service = new IndexLifecycleService(
            manager,
            new StaticLocationProvider(root));
        await service.InitializeAsync();

        service.Dispose();

        Assert.Throws<ObjectDisposedException>(() => service.GetStats());
    }

    private sealed class StaticLocationProvider(string root) : IIndexedLocationProvider
    {
        public IndexLocations Resolve() => new(root, new[] { root });
    }
}
