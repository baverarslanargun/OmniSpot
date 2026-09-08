using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class CompactCatalogReconciliationTests
{
    [Fact]
    public async Task ScopedEnsureSyncedKeepsConfiguredParentForNewNestedTree()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var existingParent = workspace.CreateDirectory(Path.Combine("root", "existing"));
        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = CreateCompactManager(database, watcher);
        await manager.InitializeAsync(root);
        watcher.Stop();

        var scopedRoot = Directory.CreateDirectory(Path.Combine(existingParent, "new")).FullName;
        var nested = Directory.CreateDirectory(Path.Combine(scopedRoot, "nested")).FullName;
        var file = Path.Combine(nested, "created.txt");
        File.WriteAllText(file, "created");

        Assert.True(await manager.EnsureSyncedAsync(scopedRoot));

        var scopedItem = Assert.Single(manager.CurrentSearchState.GetAllItems(),
            item => string.Equals(item.FullPath, scopedRoot, StringComparison.OrdinalIgnoreCase));
        Assert.Equal(existingParent, scopedItem.ParentPath, ignoreCase: true);
        var existingRow = Assert.IsType<IndexedDirectory>(database.GetDirectoryByPath(existingParent));
        var scopedRow = Assert.IsType<IndexedDirectory>(database.GetDirectoryByPath(scopedRoot));
        var nestedRow = Assert.IsType<IndexedDirectory>(database.GetDirectoryByPath(nested));
        var fileRow = Assert.IsType<IndexedFile>(database.GetFileByPath(file));
        Assert.Equal(existingRow.Id, scopedRow.ParentId);
        Assert.Equal(scopedRow.Id, nestedRow.ParentId);
        Assert.Equal(nestedRow.Id, fileRow.DirectoryId);
    }

    [Fact]
    public async Task ScopedEnsureSyncedRepairsPersistedDirectoryAndFileParents()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var parent = workspace.CreateDirectory(Path.Combine("root", "parent"));
        var child = workspace.CreateDirectory(Path.Combine("root", "parent", "child"));
        var file = workspace.CreateFile(Path.Combine("root", "parent", "child", "item.txt"));
        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = CreateCompactManager(database, watcher);
        await manager.InitializeAsync(root);
        watcher.Stop();

        var rootRow = Assert.IsType<IndexedDirectory>(database.GetDirectoryByPath(root));
        var parentRow = Assert.IsType<IndexedDirectory>(database.GetDirectoryByPath(parent));
        var childRow = Assert.IsType<IndexedDirectory>(database.GetDirectoryByPath(child));
        var fileRow = Assert.IsType<IndexedFile>(database.GetFileByPath(file));
        childRow.ParentId = rootRow.Id;
        fileRow.DirectoryId = rootRow.Id;
        database.InsertDirectory(childRow);
        database.InsertFile(fileRow);

        Assert.True(await manager.EnsureSyncedAsync(child));
        Assert.Equal(parentRow.Id, database.GetDirectoryByPath(child)!.ParentId);
        Assert.True(await manager.EnsureSyncedAsync(file));
        Assert.Equal(childRow.Id, database.GetFileByPath(file)!.DirectoryId);
    }

    [Fact]
    public async Task ReconciliationReplacingDirectoryWithFileRemovesWholeOldSubtree()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var replaced = workspace.CreateDirectory(Path.Combine("root", "replace-me"));
        var child = workspace.CreateFile(Path.Combine("root", "replace-me", "nested", "old.txt"));
        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = CreateCompactManager(database, watcher);
        await manager.InitializeAsync(root);
        watcher.Stop();
        var oldReader = manager.CurrentSearchState;

        Directory.Delete(replaced, recursive: true);
        File.WriteAllText(replaced, "replacement");
        Assert.True(await manager.EnsureSyncedAsync(root));

        Assert.True(oldReader.ContainsPath(child));
        Assert.False(manager.CurrentSearchState.ContainsPath(child));
        var replacement = Assert.Single(manager.CurrentSearchState.GetAllItems(),
            item => string.Equals(item.FullPath, replaced, StringComparison.OrdinalIgnoreCase));
        Assert.False(replacement.IsDirectory);
        Assert.Null(database.GetDirectoryByPath(replaced));
        Assert.Null(database.GetFileByPath(child));
        Assert.NotNull(database.GetFileByPath(replaced));
    }

    [Fact]
    public async Task SparseThenDenseReconciliationUsesExpectedPublicationCounters()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        for (var index = 0; index < 100; index++)
            workspace.CreateFile(Path.Combine("root", $"stable-{index:D3}.txt"));
        var sparse = workspace.CreateFile(Path.Combine("root", "sparse.txt"));
        var dense = workspace.CreateDirectory(Path.Combine("root", "dense"));
        string? firstDenseChild = null;
        for (var index = 0; index < 30; index++)
        {
            var child = workspace.CreateFile(Path.Combine("root", "dense", $"child-{index:D3}.txt"));
            firstDenseChild ??= child;
        }
        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService(debounceMs: 1);
        using var manager = CreateCompactManager(database, watcher);
        await manager.InitializeAsync(root);
        watcher.Stop();

        var incremental = manager.IncrementalReconciliationPublishCount;
        var publishes = manager.GetDiagnosticsReport().RepublishCount;
        File.Delete(sparse);
        Assert.True(await manager.EnsureSyncedAsync(root));
        Assert.Equal(incremental + 1, manager.IncrementalReconciliationPublishCount);
        Assert.Equal(publishes + 1, manager.GetDiagnosticsReport().RepublishCount);

        incremental = manager.IncrementalReconciliationPublishCount;
        publishes = manager.GetDiagnosticsReport().RepublishCount;
        Directory.Delete(dense, recursive: true);
        Assert.True(await manager.EnsureSyncedAsync(root));
        Assert.Equal(incremental, manager.IncrementalReconciliationPublishCount);
        Assert.Equal(publishes + 1, manager.GetDiagnosticsReport().RepublishCount);
        Assert.False(manager.CurrentSearchState.ContainsPath(firstDenseChild!));
    }

    private static IndexManager CreateCompactManager(
        IndexDatabase database,
        FileWatcherService watcher)
    {
        var manager = new IndexManager(
            database,
            watcher,
            layout: SearchStateLayout.Compact);
        manager.NoteChangeFeedCoverage(true);
        return manager;
    }
}
