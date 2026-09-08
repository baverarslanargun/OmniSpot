using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexMetadataStorageTests
{
    public static TheoryData<SearchStateLayout> Layouts => new()
    {
        SearchStateLayout.Legacy,
        SearchStateLayout.Compact
    };

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task IncrementOpenCountPublishesNewMetadataWithoutMutatingSnapshots(
        SearchStateLayout layout)
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var file = workspace.CreateFile(Path.Combine("root", "document.txt"));
        var databasePath = Path.Combine(workspace.Path, $"{layout}-index.db");

        using (var database = new IndexDatabase(databasePath))
        using (var watcher = new FileWatcherService(debounceMs: 1))
        using (var manager = new IndexManager(database, watcher, layout: layout))
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            var oldState = manager.CurrentSearchState;
            var oldItem = Assert.Single(oldState.Get("document"));
            Assert.Equal(0, oldItem.OpenCount);

            manager.IncrementOpenCount(file);

            var currentState = manager.CurrentSearchState;
            Assert.NotSame(oldState, currentState);
            Assert.Equal(0, oldItem.OpenCount);
            Assert.Equal(0, Assert.Single(oldState.Get("document")).OpenCount);
            Assert.Equal(1, Assert.Single(currentState.Get("document")).OpenCount);
            Assert.Equal(1, Assert.IsType<FileSystemNode>(manager.GetNode(file)).Metadata!.OpenCount);

            var firstMap = manager.MetadataMap;
            Assert.DoesNotContain(root, firstMap.Keys);
            var exportedMetadata = Assert.Single(firstMap).Value;
            Assert.Equal(1, exportedMetadata.OpenCount);
            exportedMetadata.OpenCount = 99;
            exportedMetadata.SizeBytes = -1;

            var secondMap = manager.MetadataMap;
            Assert.NotSame(firstMap, secondMap);
            Assert.NotSame(exportedMetadata, secondMap[file]);
            Assert.Equal(1, secondMap[file].OpenCount);
            Assert.NotEqual(-1, secondMap[file].SizeBytes);
            Assert.Equal(1, Assert.IsType<FileSystemNode>(manager.GetNode(file)).Metadata!.OpenCount);
        }

        using var reopenedDatabase = new IndexDatabase(databasePath);
        using var reopenedWatcher = new FileWatcherService(debounceMs: 1);
        using var reopenedManager = new IndexManager(
            reopenedDatabase,
            reopenedWatcher,
            layout: layout);
        await reopenedManager.InitializeAsync(root);
        reopenedWatcher.Stop();

        Assert.Equal(1, reopenedManager.MetadataMap[file].OpenCount);
        Assert.Equal(1, Assert.Single(reopenedManager.CurrentSearchState.Get("document")).OpenCount);
    }

    [Theory]
    [MemberData(nameof(Layouts))]
    public async Task MetadataMapContainsOnlyFilesAcrossNestedDirectories(SearchStateLayout layout)
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var nested = workspace.CreateDirectory(Path.Combine("root", "nested"));
        var first = workspace.CreateFile(Path.Combine("root", "first.txt"));
        var second = workspace.CreateFile(Path.Combine("root", "nested", "second.txt"));
        using var database = new IndexDatabase(Path.Combine(workspace.Path, $"{layout}-index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = new IndexManager(database, watcher, layout: layout);

        await manager.InitializeAsync(root);
        watcher.Stop();

        var metadata = manager.MetadataMap;
        Assert.Equal(
            new[] { first, second }.OrderBy(path => path, StringComparer.OrdinalIgnoreCase),
            metadata.Keys.OrderBy(path => path, StringComparer.OrdinalIgnoreCase));
        Assert.DoesNotContain(root, metadata.Keys);
        Assert.DoesNotContain(nested, metadata.Keys);
        Assert.All(metadata.Values, value => Assert.NotNull(value));
    }
}
