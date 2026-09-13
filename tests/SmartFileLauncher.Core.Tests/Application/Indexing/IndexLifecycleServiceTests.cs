using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Indexing;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class IndexLifecycleServiceTests
{
    [Theory]
    [InlineData(@"C:\", false)]
    [InlineData(@"D:\", false)]
    [InlineData(@"C:\Users", true)]
    [InlineData(@"C:\Users\TestUser\Downloads", true)]
    [InlineData(@"\\server\share\", true)]
    public void DirectorySelectionKeepsMftForLocalVolumeRoots(string root, bool expected)
    {
        Assert.Equal(expected, IndexLifecycleService.ShouldUseDirectoryEnumeration([root]));
    }

    [Theory]
    [InlineData(true, 0)]
    [InlineData(false, 1)]
    public async Task ScopedDirectorySelectionAvoidsReadingTheVolumeInventory(bool preferDirectory, int expectedReads)
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        workspace.CreateFile(@"root\file.txt", "metadata");
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var manager = new IndexManager(database, new FileWatcherService(), layout: SearchStateLayout.Compact);
        var source = new CountingInventorySource();
        using var service = new IndexLifecycleService(manager, new StaticLocationProvider(root),
            inventorySource: source, preferDirectoryEnumeration: preferDirectory);

        var result = await service.InitializeAsync();

        Assert.Equal(expectedReads, source.Reads);
        Assert.Equal(1, result.Stats.FileCount);
        Assert.NotNull(database.GetFileByPath(Path.Combine(root, "file.txt")));
    }

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
        Assert.Throws<ObjectDisposedException>(() => service.GetDiagnosticsReport());
    }

    private sealed class StaticLocationProvider(string root) : IIndexedLocationProvider
    {
        public IndexLocations Resolve() => new(root, new[] { root });
    }

    private sealed class CountingInventorySource : IIndexInventorySource
    {
        public int Reads { get; private set; }
        public Task<IIndexInventorySession?> ReadAsync(IReadOnlyList<string> roots,
            Action<IndexInventoryEntry> receive, CancellationToken cancellationToken)
        {
            Reads++;
            return Task.FromResult<IIndexInventorySession?>(null);
        }
    }
}
