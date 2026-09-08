using SmartFileLauncher.Core.Models;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Models;

public sealed class FileSystemNodeTests
{
    [Fact]
    public void NewNodesExposeEmptyChildrenAndFilesStillAcceptChildren()
    {
        var directory = Node("root", isDirectory: true);
        var file = Node("file.txt", isDirectory: false);
        var child = Node("child.txt", isDirectory: false);

        Assert.Empty(directory.Children);
        Assert.Empty(file.Children);
        Assert.False(file.RemoveChild(child.FullPath));

        file.AddChild(child);

        Assert.Same(file, child.Parent);
        Assert.Same(child, Assert.Single(file.Children));
    }

    [Fact]
    public void ChildrenReturnsSnapshotUnaffectedByLaterMutations()
    {
        var parent = Node("root", isDirectory: true);
        var first = Node("first.txt", isDirectory: false);
        var second = Node("second.txt", isDirectory: false);
        parent.AddChild(first);

        var snapshot = parent.Children;
        parent.AddChild(second);
        Assert.True(parent.RemoveChild(first.FullPath));

        Assert.Same(first, Assert.Single(snapshot));
        Assert.Same(second, Assert.Single(parent.Children));
    }

    [Fact]
    public void RemoveChildMatchesPathsWithoutCaseSensitivity()
    {
        var parent = Node("root", isDirectory: true);
        var child = new FileSystemNode("Report.txt", @"C:\Root\Report.txt", false);
        parent.AddChild(child);

        Assert.True(parent.RemoveChild(@"c:\root\report.TXT"));
        Assert.Empty(parent.Children);
        Assert.False(parent.RemoveChild(child.FullPath));
    }

    [Fact]
    public async Task ConcurrentFirstAddsAndReadsRetainEveryDistinctChild()
    {
        const int childCount = 256;
        var parent = Node("root", isDirectory: true);
        var children = Enumerable.Range(0, childCount)
            .Select(index => new FileSystemNode(
                $"child-{index}.txt",
                $@"C:\root\child-{index}.txt",
                false))
            .ToArray();

        using var start = new ManualResetEventSlim();
        var writers = children.Select(child => Task.Run(() =>
        {
            start.Wait();
            parent.AddChild(child);
        })).ToArray();
        var allWriters = Task.WhenAll(writers);
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            while (!allWriters.IsCompleted)
            {
                _ = parent.Children.Count;
            }
        })).ToArray();

        start.Set();
        await Task.WhenAll(writers.Concat(readers));

        var snapshot = parent.Children;
        Assert.Equal(childCount, snapshot.Count);
        Assert.Equal(childCount, snapshot.Select(child => child.FullPath).Distinct().Count());
        Assert.All(children, child => Assert.Same(parent, child.Parent));
    }

    [Fact]
    public async Task ConcurrentFirstReadsOfLazyChildrenReturnTheSameCompleteSnapshot()
    {
        var child = Node("child.txt", isDirectory: false);
        var parents = Enumerable.Range(0, 256)
            .Select(index => new FileSystemNode(
                $"parent-{index}", $@"C:\root\parent-{index}", true, null,
                () => new[] { child }))
            .ToArray();
        using var start = new ManualResetEventSlim();
        var readers = Enumerable.Range(0, 8).Select(_ => Task.Run(() =>
        {
            start.Wait();
            foreach (var parent in parents)
                Assert.Same(child, Assert.Single(parent.Children));
        })).ToArray();

        start.Set();
        await Task.WhenAll(readers);

        Assert.All(parents, parent => Assert.Same(child, Assert.Single(parent.Children)));
    }

    private static FileSystemNode Node(string name, bool isDirectory) =>
        new(name, Path.Combine(@"C:\root", name), isDirectory);
}
