using System.Collections;
using System.Reflection;
using Microsoft.Data.Sqlite;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class CanonicalCatalogIntegrationTests
{
    [Fact]
    public async Task CompactOwnerDoesNotRetainNodeTreeAndViewsAreCoherentSnapshots()
    {
        using var world = await World.CreateAsync();
        var snapshotRoot = Assert.Single(world.Manager.GetIndexedRootNodes());
        var child = Assert.Single(snapshotRoot.Children);
        Assert.Same(snapshotRoot, child.Parent);
        Assert.Same(child, Assert.Single(snapshotRoot.Children));
        var oldReader = world.Manager.CurrentSearchState;
        world.Manager.IncrementOpenCount(world.File);
        Assert.Equal(0, child.Metadata!.OpenCount);
        Assert.Equal(1, world.Manager.GetNode(world.File)!.Metadata!.OpenCount);
        Assert.Equal(0, Assert.Single(oldReader.Get("original")).OpenCount);
        Assert.Empty((IDictionary)typeof(IndexManager).GetField("_pathToNode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world.Manager)!);
        Assert.Null(typeof(IndexManager).GetField("_rootNode", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(world.Manager));
    }

    [Fact]
    public async Task FailedRenameRollsBackDatabaseAndKeepsPublishedSnapshotUntilReplay()
    {
        using var world = await World.CreateAsync();
        var oldState = world.Manager.CurrentSearchState;
        var renamed = Path.Combine(world.Root, "renamed.txt");
        File.Move(world.File, renamed);
        world.Sql("CREATE TRIGGER reject_file BEFORE INSERT ON Files BEGIN SELECT RAISE(ABORT, 'injected insert failure'); END;");
        Assert.False(world.Apply(FileChangeType.Renamed, renamed, world.File));
        Assert.Same(oldState, world.Manager.CurrentSearchState);
        Assert.NotNull(world.Database.GetFileByPath(world.File));
        Assert.Null(world.Database.GetFileByPath(renamed));
        world.Sql("DROP TRIGGER reject_file;");
        Assert.True(world.Apply(FileChangeType.Renamed, renamed, world.File));
        Assert.False(world.Manager.CurrentSearchState.ContainsPath(world.File));
        Assert.True(world.Manager.CurrentSearchState.ContainsPath(renamed));
        Assert.True(oldState.ContainsPath(world.File));
        Assert.Null(world.Database.GetFileByPath(world.File));
        Assert.NotNull(world.Database.GetFileByPath(renamed));
        var afterReplay = world.Manager.CurrentSearchState.GetAllItems().OrderBy(x => x.FullPath).ToArray();
        Assert.True(world.Apply(FileChangeType.Renamed, renamed, world.File));
        Assert.Equal(afterReplay, world.Manager.CurrentSearchState.GetAllItems().OrderBy(x => x.FullPath));
    }

    [Theory]
    [InlineData(FileChangeType.Created)]
    [InlineData(FileChangeType.Modified)]
    [InlineData(FileChangeType.Deleted)]
    public async Task FailedDatabaseMutationNeverPublishesCandidate(FileChangeType type)
    {
        using var world = await World.CreateAsync();
        var path = type == FileChangeType.Created ? Path.Combine(world.Root, "new.txt") : world.File;
        if (type == FileChangeType.Deleted) File.Delete(path);
        else File.WriteAllText(path, "new contents, with changed size");
        var oldState = world.Manager.CurrentSearchState;
        var operation = type == FileChangeType.Deleted ? "DELETE" : "INSERT";
        world.Sql($"CREATE TRIGGER reject_mutation BEFORE {operation} ON Files BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        Assert.False(world.Apply(type, path));
        Assert.Same(oldState, world.Manager.CurrentSearchState);
        Assert.Equal(1, world.Database.GetFileCount());
        world.Sql("DROP TRIGGER reject_mutation;");
        Assert.True(world.Apply(type, path));
    }

    [Fact]
    public async Task CandidateConstructionFailureRollsBackOpenCountAndAllowsRetry()
    {
        var tokenizer = new FailingTokenizer();
        using var world = await World.CreateAsync(tokenizer);
        var oldState = world.Manager.CurrentSearchState;
        tokenizer.Fail = true;
        Assert.Throws<InvalidOperationException>(() => world.Manager.IncrementOpenCount(world.File));
        Assert.Same(oldState, world.Manager.CurrentSearchState);
        Assert.Equal(0, world.Database.GetFileByPath(world.File)!.OpenCount);
        tokenizer.Fail = false;
        world.Manager.IncrementOpenCount(world.File);
        Assert.Equal(1, world.Database.GetFileByPath(world.File)!.OpenCount);
        Assert.Equal(1, Assert.Single(world.Manager.CurrentSearchState.Get("original")).OpenCount);
    }

    [Fact]
    public async Task FailedReconciliationIsAtomicAndRetryRepairsNestedChanges()
    {
        using var world = await World.CreateAsync();
        var oldState = world.Manager.CurrentSearchState;
        File.Delete(world.File);
        var directory = Directory.CreateDirectory(Path.Combine(world.Root, "nested")).FullName;
        var created = Path.Combine(directory, "created.pdf");
        File.WriteAllText(created, "document");
        world.Sql("CREATE TRIGGER reject_file BEFORE INSERT ON Files BEGIN SELECT RAISE(ABORT, 'injected failure'); END;");
        Assert.False(await world.Manager.ReconcileWithinLifecycleAsync(world.Root));
        Assert.Same(oldState, world.Manager.CurrentSearchState);
        Assert.NotNull(world.Database.GetFileByPath(world.File));
        Assert.Null(world.Database.GetDirectoryByPath(directory));
        world.Sql("DROP TRIGGER reject_file;");
        Assert.True(await world.Manager.ReconcileWithinLifecycleAsync(world.Root));
        Assert.False(world.Manager.CurrentSearchState.ContainsPath(world.File));
        Assert.True(world.Manager.CurrentSearchState.ContainsPath(created));
        Assert.NotNull(world.Database.GetFileByPath(created));
        Assert.Equal(0L, world.Scalar("SELECT COUNT(*) FROM pragma_foreign_key_check"));
    }

    [Fact]
    public async Task FailedRescanPreservesExistingIndex()
    {
        var tokenizer = new FailingTokenizer();
        using var world = await World.CreateAsync(tokenizer);
        var oldState = world.Manager.CurrentSearchState;
        tokenizer.Fail = true;
        await Assert.ThrowsAsync<InvalidOperationException>(() => world.Manager.RescanAsync(world.Root));
        Assert.Same(oldState, world.Manager.CurrentSearchState);
        Assert.NotNull(world.Database.GetFileByPath(world.File));
    }

    private sealed class FailingTokenizer : ITokenizer
    {
        internal bool Fail;
        public IEnumerable<string> Tokenize(string input) => Fail
            ? throw new InvalidOperationException("injected candidate failure")
            : new BasicTokenizer().Tokenize(input);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseOnlyRenamePreservesDatabaseIdsAndOpenCounts(bool viaEvent)
    {
        using var world = await World.CreateAsync();
        world.Manager.IncrementOpenCount(world.File);
        var original = world.Database.GetFileByPath(world.File)!;
        world.Sql($"INSERT INTO Tokens(Id, Token) VALUES (1, 'original'); INSERT INTO FileTokens(FileId, TokenId) VALUES ({original.Id}, 1);");
        var temporary = Path.Combine(world.Root, "temporary.txt");
        var renamed = Path.Combine(world.Root, "Original.TXT");
        File.Move(world.File, temporary);
        File.Move(temporary, renamed);
        Assert.True(viaEvent
            ? world.Apply(FileChangeType.Renamed, renamed, world.File)
            : await world.Manager.ReconcileWithinLifecycleAsync(world.Root));
        var row = world.Database.GetFileByPath(renamed);
        Assert.NotNull(row);
        Assert.Equal(original.Id, row.Id);
        Assert.Equal(1, row.OpenCount);
        Assert.Equal(original.CreatedTimeUtc, row.CreatedTimeUtc);
        Assert.Equal(1, world.Database.GetFileCount());
        Assert.Equal(1L, world.Scalar($"SELECT COUNT(*) FROM FileTokens WHERE FileId = {original.Id}"));
        Assert.Equal(renamed, Assert.Single(world.Manager.CurrentSearchState.Get("original")).FullPath);
        Assert.Equal(renamed, world.Manager.GetNode(renamed)!.FullPath);
        Assert.True(world.Apply(FileChangeType.Modified, renamed));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CaseOnlyDirectoryRenameKeepsParentIdsAndAllChildren(bool viaEvent)
    {
        using var world = await World.CreateAsync();
        var original = Directory.CreateDirectory(Path.Combine(world.Root, "folder")).FullName;
        var child = Path.Combine(original, "child.txt");
        File.WriteAllText(child, "child");
        Assert.True(await world.Manager.ReconcileWithinLifecycleAsync(world.Root));
        var directoryId = world.Database.GetDirectoryByPath(original)!.Id;
        var fileId = world.Database.GetFileByPath(child)!.Id;
        world.Manager.IncrementOpenCount(child);
        world.Sql($"INSERT INTO Tokens(Id, Token) VALUES (1, 'child'); INSERT INTO FileTokens(FileId, TokenId) VALUES ({fileId}, 1);");
        var renamed = Path.Combine(world.Root, "FOLDER");
        var temporary = Path.Combine(world.Root, "transit");
        Directory.Move(original, temporary);
        Directory.Move(temporary, renamed);
        Assert.True(viaEvent
            ? world.Apply(FileChangeType.Renamed, renamed, original)
            : await world.Manager.ReconcileWithinLifecycleAsync(world.Root));
        var renamedChild = Path.Combine(renamed, "child.txt");
        Assert.Equal(directoryId, world.Database.GetDirectoryByPath(renamed)!.Id);
        Assert.Equal(fileId, world.Database.GetFileByPath(renamedChild)!.Id);
        Assert.Equal(directoryId, world.Database.GetFileByPath(renamedChild)!.DirectoryId);
        Assert.Equal(1, world.Database.GetFileByPath(renamedChild)!.OpenCount);
        Assert.Equal(1L, world.Scalar($"SELECT COUNT(*) FROM FileTokens WHERE FileId = {fileId}"));
        Assert.Equal(renamed, Assert.Single(world.Manager.CurrentSearchState.Get("folder")).FullPath);
        Assert.Equal(renamedChild, Assert.Single(world.Manager.CurrentSearchState.Get("child")).FullPath);
        Assert.Equal(2, world.Database.GetFileCount());
        Assert.Equal(2, world.Database.GetDirectoryCount());
    }

    [Fact]
    public async Task RenameOutsideIndexedRootsRemovesOldPathAndReplaySucceeds()
    {
        using var world = await World.CreateAsync();
        var outside = Path.Combine(Path.GetDirectoryName(world.Root)!, "outside.txt");
        File.Move(world.File, outside);
        var previous = world.Manager.CurrentSearchState;
        Assert.True(world.Apply(FileChangeType.Renamed, outside, world.File));
        Assert.True(previous.ContainsPath(world.File));
        Assert.False(world.Manager.CurrentSearchState.ContainsPath(world.File));
        Assert.False(world.Manager.CurrentSearchState.ContainsPath(outside));
        Assert.Equal(0, world.Database.GetFileCount());
        Assert.True(world.Apply(FileChangeType.Renamed, outside, world.File));
        Assert.True(File.Exists(outside));
    }

    [Fact]
    public async Task ReplayedModificationFollowedByDeletionDoesNotRequestAFullRescan()
    {
        using var world = await World.CreateAsync();
        File.Delete(world.File);
        var errors = new System.Collections.Concurrent.ConcurrentQueue<string>();
        world.Manager.OnError += errors.Enqueue;
        Assert.True(world.Manager.ApplyExternalChanges([
            new() { ChangeType = FileChangeType.Modified, FullPath = world.File },
            new() { ChangeType = FileChangeType.Deleted, FullPath = world.File.Replace('\\', '/') }
        ]));
        Assert.False(world.Manager.CurrentSearchState.ContainsPath(world.File));
        Assert.Null(world.Database.GetFileByPath(world.File));
        Assert.Empty(errors);
    }

    [Fact]
    public async Task ATransientCreatedFileWithAnAlreadyRemovedParentDoesNotRequestAFullRescan()
    {
        using var world = await World.CreateAsync();
        var gone = Path.Combine(world.Root, "already-removed", "temporary.txt");
        var previous = world.Manager.CurrentSearchState;
        Assert.True(world.Manager.ApplyExternalChanges([
            new() { ChangeType = FileChangeType.Created, FullPath = gone },
            new() { ChangeType = FileChangeType.Deleted, FullPath = gone }
        ]));
        Assert.Equal(previous.GetAllItems(), world.Manager.CurrentSearchState.GetAllItems());
        Assert.Null(world.Database.GetFileByPath(gone));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ADeletedDifferentPathDoesNotHideAnUnresolvedModification(bool includeOtherDelete)
    {
        using var world = await World.CreateAsync();
        File.Delete(world.File);
        var events = new List<FileChangeEvent> {
            new() { ChangeType = FileChangeType.Modified, FullPath = world.File }
        };
        if (includeOtherDelete)
            events.Add(new() { ChangeType = FileChangeType.Deleted, FullPath = Path.Combine(world.Root, "other.txt") });
        Assert.False(world.Manager.ApplyExternalChanges(events));
        Assert.True(world.Manager.CurrentSearchState.ContainsPath(world.File));
        Assert.NotNull(world.Database.GetFileByPath(world.File));
    }

    [Theory]
    [InlineData(FileChangeType.Created, FileAttributes.Hidden)]
    [InlineData(FileChangeType.Created, FileAttributes.System)]
    [InlineData(FileChangeType.Modified, FileAttributes.Hidden)]
    [InlineData(FileChangeType.Modified, FileAttributes.System)]
    public async Task ExcludedFilesDoNotRequestAFullRescan(FileChangeType type, FileAttributes attribute)
    {
        using var world = await World.CreateAsync();
        var excluded = Path.Combine(world.Root, "excluded.log");
        File.WriteAllText(excluded, "temporary");
        File.SetAttributes(excluded, attribute);
        try
        {
            Assert.True(world.Apply(type, excluded));
            Assert.False(world.Manager.CurrentSearchState.ContainsPath(excluded));
            Assert.Null(world.Database.GetFileByPath(excluded));
            Assert.True(world.Manager.CurrentSearchState.ContainsPath(world.File));
        }
        finally { File.SetAttributes(excluded, FileAttributes.Normal); }
    }

    [Fact]
    public async Task AnEventBelowAnExcludedDirectoryDoesNotIntroduceItsChild()
    {
        using var world = await World.CreateAsync();
        var excluded = Directory.CreateDirectory(Path.Combine(world.Root, "excluded")).FullName;
        var child = Path.Combine(excluded, "child.txt");
        File.WriteAllText(child, "child");
        File.SetAttributes(excluded, FileAttributes.Hidden);
        try
        {
            Assert.True(world.Apply(FileChangeType.Created, child));
            Assert.True(world.Apply(FileChangeType.Modified, child));
            Assert.False(world.Manager.CurrentSearchState.ContainsPath(child));
            Assert.Null(world.Database.GetFileByPath(child));
        }
        finally { File.SetAttributes(excluded, FileAttributes.Normal); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AFileBecomingExcludedIsRemovedFromBothCatalogAndDatabase(bool rename)
    {
        using var world = await World.CreateAsync();
        var target = rename ? Path.Combine(world.Root, "hidden.txt") : world.File;
        if (rename) File.Move(world.File, target);
        File.SetAttributes(target, FileAttributes.Hidden);
        try
        {
            Assert.True(world.Apply(rename ? FileChangeType.Renamed : FileChangeType.Modified,
                target, rename ? world.File : null));
            Assert.False(world.Manager.CurrentSearchState.ContainsPath(world.File));
            Assert.False(world.Manager.CurrentSearchState.ContainsPath(target));
            Assert.Null(world.Database.GetFileByPath(world.File));
            Assert.Null(world.Database.GetFileByPath(target));
        }
        finally { File.SetAttributes(target, FileAttributes.Normal); }
    }

    [Fact]
    public async Task AnExplicitHiddenRootStillAcceptsVisibleFileChanges()
    {
        using var world = await World.CreateAsync();
        File.SetAttributes(world.Root, FileAttributes.Hidden);
        try
        {
            File.WriteAllText(world.File, "changed content");
            Assert.True(world.Apply(FileChangeType.Modified, world.File));
            Assert.Equal(new FileInfo(world.File).Length, world.Database.GetFileByPath(world.File)!.SizeBytes);
            Assert.True(world.Manager.CurrentSearchState.ContainsPath(world.File));
        }
        finally { File.SetAttributes(world.Root, FileAttributes.Normal); }
    }

    [Fact]
    public async Task RecoveringAMissingSubtreeRemovesOnlyThatSubtree()
    {
        using var world = await World.CreateAsync();
        var directory = Directory.CreateDirectory(Path.Combine(world.Root, "temporary")).FullName;
        var child = Path.Combine(directory, "child.txt");
        File.WriteAllText(child, "child");
        Assert.True(world.Apply(FileChangeType.Created, directory));
        Directory.Delete(directory, true);
        Assert.True(await world.Manager.ReconcileWithinLifecycleAsync(directory));
        Assert.False(world.Manager.CurrentSearchState.ContainsPath(directory));
        Assert.False(world.Manager.CurrentSearchState.ContainsPath(child));
        Assert.Null(world.Database.GetDirectoryByPath(directory));
        Assert.Null(world.Database.GetFileByPath(child));
        Assert.True(world.Manager.CurrentSearchState.ContainsPath(world.File));
        Assert.NotNull(world.Database.GetFileByPath(world.File));
    }

    private sealed class World : IDisposable
    {
        private readonly TemporaryDirectory _directory;
        internal IndexDatabase Database { get; }
        internal IndexManager Manager { get; }
        internal string Root { get; }
        internal string File { get; }
        private World(TemporaryDirectory directory, IndexDatabase database, IndexManager manager, string root, string file)
        { _directory = directory; Database = database; Manager = manager; Root = root; File = file; }

        internal static async Task<World> CreateAsync(ITokenizer? tokenizer = null)
        {
            var directory = new TemporaryDirectory();
            var root = directory.CreateDirectory("root");
            var file = directory.CreateFile(Path.Combine("root", "original.txt"));
            var database = new IndexDatabase(Path.Combine(directory.Path, "index.db"));
            var watcher = new FileWatcherService(debounceMs: 1);
            var manager = new IndexManager(database, watcher, tokenizer, layout: SearchStateLayout.Compact);
            manager.NoteChangeFeedCoverage(true);
            await manager.InitializeAsync(root);
            watcher.Stop();
            return new World(directory, database, manager, root, file);
        }

        internal bool Apply(FileChangeType type, string path, string? oldPath = null) =>
            Manager.ApplyExternalChanges([new() { ChangeType = type, FullPath = path, OldPath = oldPath }]);

        internal void Sql(string sql) => Scalar(sql);
        internal object? Scalar(string sql)
        {
            using var connection = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database.DatabasePath, Pooling = false }.ToString());
            connection.Open();
            using var command = connection.CreateCommand();
            command.CommandText = sql;
            return command.ExecuteScalar();
        }
        public void Dispose() { Manager.Dispose(); _directory.Dispose(); }
    }
}
