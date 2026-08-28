using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexManagerFileChangeTests
{
    [Fact]
    public async Task DeletedDottedDirectory_RemovesWholeSubtreeWhenEventSaysFile()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var dottedDirectory = workspace.CreateDirectory(Path.Combine("root", "folder.with.dot"));
        var nestedDirectory = workspace.CreateDirectory(Path.Combine("root", "folder.with.dot", "nested"));
        var deletedFile = workspace.CreateFile(Path.Combine("root", "folder.with.dot", "nested", "ghost.txt"));
        var siblingFile = workspace.CreateFile(Path.Combine("root", "folder.with.dot-sibling", "keep.txt"));
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            Directory.Delete(dottedDirectory, recursive: true);
            manager.ApplyFileChange(new FileChangeEvent
            {
                ChangeType = FileChangeType.Deleted,
                FullPath = dottedDirectory.Replace(
                    Path.DirectorySeparatorChar,
                    Path.AltDirectorySeparatorChar),
                IsDirectory = false
            });

            Assert.Null(manager.GetNode(dottedDirectory));
            Assert.Null(manager.GetNode(nestedDirectory));
            Assert.Null(manager.GetNode(deletedFile));
            Assert.False(manager.MetadataMap.ContainsKey(deletedFile));
            Assert.Empty(manager.CurrentSearchState.Get("ghost"));
            Assert.Null(database.GetDirectoryByPath(dottedDirectory));
            Assert.Null(database.GetDirectoryByPath(nestedDirectory));
            Assert.Null(database.GetFileByPath(deletedFile));

            Assert.NotNull(manager.GetNode(siblingFile));
            Assert.NotNull(database.GetFileByPath(siblingFile));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task DeletedExtensionlessFile_RemovesOnlyFileWhenEventSaysDirectory()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var extensionlessFile = workspace.CreateFile(Path.Combine("root", "LICENSE"));
        var siblingFile = workspace.CreateFile(Path.Combine("root", "keep.txt"));
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            File.Delete(extensionlessFile);
            manager.ApplyFileChange(new FileChangeEvent
            {
                ChangeType = FileChangeType.Deleted,
                FullPath = extensionlessFile,
                IsDirectory = true
            });

            Assert.Null(manager.GetNode(extensionlessFile));
            Assert.NotNull(manager.GetNode(siblingFile));
            Assert.NotNull(manager.GetNode(root));
            Assert.Null(database.GetFileByPath(extensionlessFile));
            Assert.NotNull(database.GetFileByPath(siblingFile));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task RenamedDirectory_ReindexesDescendantsAndIgnoresDuplicateCreatedEvent()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var oldDirectory = workspace.CreateDirectory(Path.Combine("root", "old"));
        var oldChild = workspace.CreateDirectory(Path.Combine("root", "old", "child"));
        var oldFile = workspace.CreateFile(Path.Combine("root", "old", "child", "document.txt"));
        var newDirectory = Path.Combine(root, "new");
        var newChild = Path.Combine(newDirectory, "child");
        var newFile = Path.Combine(newChild, "document.txt");
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            Directory.Move(oldDirectory, newDirectory);
            manager.ApplyFileChange(new FileChangeEvent
            {
                ChangeType = FileChangeType.Renamed,
                OldPath = oldDirectory,
                FullPath = newDirectory,
                IsDirectory = false
            });

            Assert.Null(manager.GetNode(oldDirectory));
            Assert.Null(manager.GetNode(oldChild));
            Assert.Null(manager.GetNode(oldFile));
            Assert.NotNull(manager.GetNode(newDirectory));
            Assert.NotNull(manager.GetNode(newChild));
            Assert.NotNull(manager.GetNode(newFile));
            Assert.Null(database.GetDirectoryByPath(oldDirectory));
            Assert.Null(database.GetFileByPath(oldFile));

            var persistedDirectory = Assert.IsType<IndexedDirectory>(database.GetDirectoryByPath(newDirectory));
            var persistedChild = Assert.IsType<IndexedDirectory>(database.GetDirectoryByPath(newChild));
            var persistedFile = Assert.IsType<IndexedFile>(database.GetFileByPath(newFile));
            Assert.Equal(persistedDirectory.Id, persistedChild.ParentId);
            Assert.Equal(persistedChild.Id, persistedFile.DirectoryId);

            var indexedNodeCount = manager.IndexedFileCount;
            manager.ApplyFileChange(new FileChangeEvent
            {
                ChangeType = FileChangeType.Created,
                FullPath = newDirectory,
                IsDirectory = true
            });
            Assert.Equal(indexedNodeCount, manager.IndexedFileCount);

            var rootNode = Assert.IsType<FileSystemNode>(manager.GetNode(root));
            Assert.Single(rootNode.Children, node =>
                string.Equals(node.FullPath, newDirectory, StringComparison.OrdinalIgnoreCase));
            var searchState = manager.CreateSearchState();
            Assert.Empty(searchState.Get("old"));
            Assert.Contains(searchState.Get("new"), item =>
                string.Equals(item.FullPath, newDirectory, StringComparison.OrdinalIgnoreCase));
            Assert.Single(searchState.Get("document"), item =>
                string.Equals(item.FullPath, newFile, StringComparison.OrdinalIgnoreCase));
            Assert.DoesNotContain(searchState.Get("document"), item =>
                string.Equals(item.FullPath, oldFile, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task IncrementOpenCount_UpdatesMemoryAndSurvivesDatabaseReopen()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var file = workspace.CreateFile(Path.Combine("root", "document.txt"));
        var databasePath = Path.Combine(workspace.Path, "index.db");
        var database = new IndexDatabase(databasePath);
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();
            manager.IncrementOpenCount(file);
            manager.IncrementOpenCount(file);

            Assert.Equal(2, manager.MetadataMap[file].OpenCount);
        }
        finally
        {
            manager.Dispose();
        }

        using var reopenedDatabase = new IndexDatabase(databasePath);
        reopenedDatabase.Open();
        Assert.Equal(2, reopenedDatabase.GetFileByPath(file)?.OpenCount);
    }

    [Fact]
    public async Task DeleteDatabaseFailure_DoesNotMutateTheInMemoryIndex()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var file = workspace.CreateFile(Path.Combine("root", "document.txt"));
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();
            var stateBeforeFailure = manager.CreateSearchState();
            database.Close();
            File.Delete(file);

            manager.ApplyFileChange(new FileChangeEvent
            {
                ChangeType = FileChangeType.Deleted,
                FullPath = file,
                IsDirectory = false
            });

            Assert.NotNull(manager.GetNode(file));
            Assert.True(manager.MetadataMap.ContainsKey(file));
            var stateAfterFailure = manager.CreateSearchState();
            Assert.NotSame(stateBeforeFailure, stateAfterFailure);
            Assert.Contains(stateAfterFailure.Get("document"), item =>
                string.Equals(item.FullPath, file, StringComparison.OrdinalIgnoreCase));
        }
        finally
        {
            manager.Dispose();
        }
    }
    [Fact]
    public async Task SearchState_PublishesImmutableVersionsForIndexAndFrequencyChanges()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var document = workspace.CreateFile(Path.Combine("root", "document.txt"));
        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            var initial = manager.CreateSearchState();
            Assert.Same(initial, manager.CreateSearchState());
            Assert.Equal(0, Assert.Single(initial.Get("document")).OpenCount);

            manager.IncrementOpenCount(document);
            var afterOpen = manager.CreateSearchState();
            Assert.NotSame(initial, afterOpen);
            Assert.Equal(0, Assert.Single(initial.Get("document")).OpenCount);
            Assert.Equal(1, Assert.Single(afterOpen.Get("document")).OpenCount);

            var added = workspace.CreateFile(Path.Combine("root", "new-document.txt"));
            manager.ApplyFileChange(new FileChangeEvent
            {
                ChangeType = FileChangeType.Created,
                FullPath = added,
                IsDirectory = false
            });
            var afterCreate = manager.CreateSearchState();
            Assert.Empty(afterOpen.Get("new"));
            Assert.Contains(afterCreate.Get("new"), item =>
                string.Equals(item.FullPath, added, StringComparison.OrdinalIgnoreCase));

            File.Delete(added);
            manager.ApplyFileChange(new FileChangeEvent
            {
                ChangeType = FileChangeType.Deleted,
                FullPath = added,
                IsDirectory = false
            });
            var afterDelete = manager.CreateSearchState();
            Assert.Contains(afterCreate.Get("new"), item =>
                string.Equals(item.FullPath, added, StringComparison.OrdinalIgnoreCase));
            Assert.Empty(afterDelete.Get("new"));
        }
        finally
        {
            manager.Dispose();
        }
    }

    [Fact]
    public async Task DeletedSubtreeClearsEveryLayerAndKeepsPrefixSibling()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var target = workspace.CreateDirectory(Path.Combine("root", "alpha-target"));
        var nested = workspace.CreateDirectory(Path.Combine("root", "alpha-target", "deep"));
        var targetFile = workspace.CreateFile(Path.Combine("root", "alpha-target", "zetamark-one.txt"));
        var nestedFile = workspace.CreateFile(
            Path.Combine("root", "alpha-target", "deep", "zetamark-two.txt"));
        var siblingFile = workspace.CreateFile(
            Path.Combine("root", "alpha-target-sibling", "zetamark-keep.txt"));

        var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher);

        try
        {
            await manager.InitializeAsync(root);
            watcher.Stop();

            var before = manager.CreateSearchState();
            Assert.Equal(3, before.Get("zetamark").Count);

            Directory.Delete(target, recursive: true);
            manager.ApplyFileChange(new FileChangeEvent
            {
                ChangeType = FileChangeType.Deleted,
                FullPath = target,
                IsDirectory = true
            });

            Assert.Null(manager.GetNode(target));
            Assert.Null(manager.GetNode(nested));
            Assert.Null(manager.GetNode(targetFile));
            Assert.Null(manager.GetNode(nestedFile));

            Assert.False(manager.MetadataMap.ContainsKey(targetFile));
            Assert.False(manager.MetadataMap.ContainsKey(nestedFile));
            Assert.Null(database.GetDirectoryByPath(target));
            Assert.Null(database.GetDirectoryByPath(nested));
            Assert.Null(database.GetFileByPath(targetFile));
            Assert.Null(database.GetFileByPath(nestedFile));

            var after = manager.CurrentSearchState;
            var survivors = after.Get("zetamark");
            Assert.Single(survivors);
            Assert.Contains(survivors, item =>
                string.Equals(item.FullPath, siblingFile, StringComparison.OrdinalIgnoreCase));

            Assert.NotNull(manager.GetNode(siblingFile));
            Assert.NotNull(database.GetFileByPath(siblingFile));
        }
        finally
        {
            manager.Dispose();
        }
    }
}
