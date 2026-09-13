using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class CompactDirectoryEnumerationTests
{
    [Fact]
    public async Task ExplicitHiddenRootIsIndexedWhileNestedHiddenAndSystemEntriesAreExcluded()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var visible = workspace.CreateFile(@"root\visible.txt", "visible");
        var hiddenFile = workspace.CreateFile(@"root\hidden.txt", "hidden");
        var systemFile = workspace.CreateFile(@"root\system.txt", "system");
        var hiddenDirectory = workspace.CreateDirectory(@"root\hidden-dir");
        var hiddenChild = workspace.CreateFile(@"root\hidden-dir\child.txt", "child");
        File.SetAttributes(root, File.GetAttributes(root) | FileAttributes.Hidden);
        File.SetAttributes(hiddenFile, File.GetAttributes(hiddenFile) | FileAttributes.Hidden);
        File.SetAttributes(systemFile, File.GetAttributes(systemFile) | FileAttributes.System);
        File.SetAttributes(hiddenDirectory, File.GetAttributes(hiddenDirectory) | FileAttributes.Hidden);
        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService();
        using var manager = new IndexManager(database, watcher, layout: SearchStateLayout.Compact);

        await manager.InitializeAsync([root]);

        Assert.NotNull(database.GetDirectoryByPath(root));
        Assert.NotNull(database.GetFileByPath(visible));
        Assert.Null(database.GetFileByPath(hiddenFile));
        Assert.Null(database.GetFileByPath(systemFile));
        Assert.Null(database.GetDirectoryByPath(hiddenDirectory));
        Assert.Null(database.GetFileByPath(hiddenChild));
    }

    [Fact]
    public async Task EnumerationPreservesFileSizeCreationModificationAndUnicodePath()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var path = workspace.CreateFile(@"root\İş ' 📄.txt", "metadata ✓");
        File.SetCreationTimeUtc(path, new DateTime(2021, 2, 3, 4, 5, 6, DateTimeKind.Utc));
        File.SetLastWriteTimeUtc(path, new DateTime(2022, 3, 4, 5, 6, 7, DateTimeKind.Utc));
        var expected = new FileInfo(path);
        expected.Refresh();
        using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
        using var watcher = new FileWatcherService();
        using var manager = new IndexManager(database, watcher, layout: SearchStateLayout.Compact);

        await manager.InitializeAsync([root]);

        var actual = database.GetFileByPath(path)!;
        Assert.NotNull(actual);
        Assert.Equal(Path.GetFileName(path), actual.FileName);
        Assert.Equal(expected.Length, actual.SizeBytes);
        Assert.Equal(expected.CreationTimeUtc.Ticks, actual.CreatedTimeUtc);
        Assert.Equal(expected.LastWriteTimeUtc.Ticks, actual.LastWriteTimeUtc);
        Assert.False(actual.IsHidden);
        Assert.False(actual.IsSystem);
    }

    [Fact]
    public async Task FollowedJunctionKeepsLexicalChildPathsAndMetadata()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var target = workspace.CreateDirectory("target");
        var physical = workspace.CreateFile(@"target\child\file.txt", "junction content");
        var link = Path.Combine(root, "link");
        WindowsDirectoryLink.CreateJunction(link, target);
        try
        {
            using var database = new IndexDatabase(Path.Combine(workspace.Path, "index.db"));
            using var watcher = new FileWatcherService();
            using var manager = new IndexManager(database, watcher, layout: SearchStateLayout.Compact);

            await manager.InitializeAsync([root]);

            var lexical = Path.Combine(link, "child", "file.txt");
            var actual = database.GetFileByPath(lexical)!;
            Assert.NotNull(actual);
            Assert.Equal(new FileInfo(physical).Length, actual.SizeBytes);
            Assert.NotNull(database.GetDirectoryByPath(Path.Combine(link, "child")));
            Assert.Null(database.GetFileByPath(physical));
            Assert.Null(database.GetDirectoryByPath(target));
        }
        finally
        {
            WindowsDirectoryLink.Delete(link);
        }
    }
}
