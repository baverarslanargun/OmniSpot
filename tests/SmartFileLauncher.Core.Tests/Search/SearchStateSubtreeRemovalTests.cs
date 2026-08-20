using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class SearchStateSubtreeRemovalTests
{
    private static readonly BasicTokenizer Tokenizer = new();

    [Fact]
    public void RemovalMatchesPrefixSemanticsForCanonicalPaths()
    {
        var state = CreateCanonicalTree();
        var target = @"C:\Root\foo";
        var expected = state.GetAllItems()
            .Select(item => item.FullPath)
            .Where(candidate => !IsTargetOrDescendant(candidate, target))
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var actual = state.WithoutPathAndDescendants(target)
            .GetAllItems()
            .Select(item => item.FullPath)
            .OrderBy(path => path, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void RemovingDirectoryDoesNotRemoveSimilarlyNamedSibling()
    {
        var state = CreateCanonicalTree();

        var trimmed = state.WithoutPathAndDescendants(@"C:\Root\foo");

        Assert.DoesNotContain(trimmed.GetAllItems(), item => item.FullPath == @"C:\Root\foo\foo-child.txt");
        Assert.Contains(trimmed.GetAllItems(), item => item.FullPath == @"C:\Root\foobar\foobar-child.txt");
    }

    [Fact]
    public void RemovingDriveRootRemovesAllItems()
    {
        var root = new FileSystemNode("C", @"C:\", true);
        var child = new FileSystemNode("root-file.txt", @"C:\root-file.txt", false);
        root.AddChild(child);
        var state = SearchState.Create([root, child], Tokenizer);

        var trimmed = state.WithoutPathAndDescendants(@"C:\");

        Assert.Empty(trimmed.GetAllItems());
    }

    [Fact]
    public void RemovalAcceptsTrailingSeparatorAndDifferentCasing()
    {
        var state = CreateCanonicalTree();

        var trimmed = state.WithoutPathAndDescendants(@"c:\ROOT\FOO\");

        Assert.DoesNotContain(trimmed.GetAllItems(), item => item.FullPath == @"C:\Root\foo\foo-child.txt");
        Assert.Contains(trimmed.GetAllItems(), item => item.FullPath == @"C:\Root\foobar\foobar-child.txt");
    }

    [Fact]
    public void RemovalFollowsAlternateSeparatorChildEdge()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var folder = new FileSystemNode("folder", @"C:\Root\folder", true);
        var child = new FileSystemNode("mixed-child.txt", @"C:\Root\folder/mixed-child.txt", false);
        root.AddChild(folder);
        folder.AddChild(child);
        var state = SearchState.Create([root, folder, child], Tokenizer);

        var trimmed = state.WithoutPathAndDescendants(folder.FullPath);

        Assert.DoesNotContain(trimmed.GetAllItems(), item => item.FullPath == @"C:\Root\folder/mixed-child.txt");
        Assert.Single(trimmed.GetAllItems());
    }

    [Fact]
    public void RemovalFollowsPendingEdgeWhenParentItemIsMissing()
    {
        var parent = new FileSystemNode("missing", @"C:\Root\missing", true);
        var child = new FileSystemNode("pending-child.txt", @"C:\Root\missing\pending-child.txt", false);
        parent.AddChild(child);
        var state = SearchState.Create([child], Tokenizer);

        var trimmed = state.WithoutPathAndDescendants(parent.FullPath);

        Assert.Empty(trimmed.GetAllItems());
        Assert.Empty(trimmed.Get("pending-child"));
    }

    [Fact]
    public void RemovingAndReaddingParentDoesNotRestoreRemovedDescendants()
    {
        var state = CreateCanonicalTree();
        var trimmed = state.WithoutPathAndDescendants(@"C:\Root\foo");
        var replacement = new FileSystemNode("foo", @"C:\Root\foo", true);

        var restored = trimmed.WithUpserts([replacement], Tokenizer);

        var restoredParent = Assert.Single(restored.Get("foo"));
        Assert.Empty(restored.GetDescendants(restoredParent));
        Assert.DoesNotContain(restored.GetAllItems(), item => item.FullPath == @"C:\Root\foo\foo-child.txt");
    }

    [Fact]
    public void RemovalReachesDeepItemWhenAnIntermediateDirectoryIsMissing()
    {
        var (state, _) = CreateTreeWithMissingIntermediate();

        var trimmed = state.WithoutPathAndDescendants(@"C:\Root");

        Assert.Empty(trimmed.GetAllItems());
        Assert.Empty(trimmed.Get("deep"));
    }

    [Fact]
    public void CreateSelectsTheWalkPathForACompleteTree()
    {
        Assert.Equal(0, CreateCanonicalTree().MissingParentCount);
    }

    [Fact]
    public void MissingIntermediateDirectorySelectsTheScanFallback()
    {
        var (state, _) = CreateTreeWithMissingIntermediate();

        Assert.Equal(1, state.MissingParentCount);
    }

    [Fact]
    public void RemovingDriveRootRemovesAllItemsOnTheScanFallback()
    {
        var driveRoot = new FileSystemNode("C", @"C:\", true);
        var rootFile = new FileSystemNode("root-file.txt", @"C:\root-file.txt", false);
        driveRoot.AddChild(rootFile);
        var missing = new FileSystemNode("middle", @"C:\middle", true);
        var deep = new FileSystemNode("deep.txt", @"C:\middle\deep.txt", false);
        missing.AddChild(deep);

        var state = SearchState.Create([driveRoot, rootFile, deep], Tokenizer);
        Assert.NotEqual(0, state.MissingParentCount);

        var trimmed = state.WithoutPathAndDescendants(@"C:\");

        Assert.Empty(trimmed.GetAllItems());
    }

    [Fact]
    public void FillingTheMissingParentReturnsToTheWalkPath()
    {
        var (state, middle) = CreateTreeWithMissingIntermediate();

        var filled = state.WithUpserts([middle], Tokenizer);

        Assert.Equal(0, filled.MissingParentCount);
        Assert.Empty(filled.WithoutPathAndDescendants(@"C:\Root").GetAllItems());
    }

    [Fact]
    public void UpsertKeepsTheMissingParentCounterConsistent()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var middle = new FileSystemNode("middle", @"C:\Root\middle", true);
        var deep = new FileSystemNode("deep.txt", @"C:\Root\middle\deep.txt", false);
        root.AddChild(middle);
        middle.AddChild(deep);

        var orphaned = SearchState.Empty.WithUpserts([root, deep], Tokenizer);
        Assert.Equal(1, orphaned.MissingParentCount);

        var filled = orphaned.WithUpserts([middle], Tokenizer);

        Assert.Equal(0, filled.MissingParentCount);
        Assert.Equal(
            [@"C:\Root\middle", @"C:\Root\middle\deep.txt"],
            filled.GetDescendants(Assert.Single(filled.Get("Root")))
                .Select(i => i.FullPath)
                .OrderBy(p => p, StringComparer.Ordinal)
                .ToArray());
    }

    private static (SearchState State, FileSystemNode Middle) CreateTreeWithMissingIntermediate()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var middle = new FileSystemNode("middle", @"C:\Root\middle", true);
        var deep = new FileSystemNode("deep.txt", @"C:\Root\middle\deep.txt", false);
        root.AddChild(middle);
        middle.AddChild(deep);

        return (SearchState.Create([root, deep], Tokenizer), middle);
    }

    private static SearchState CreateCanonicalTree()
    {
        var root = new FileSystemNode("Root", @"C:\Root", true);
        var foo = new FileSystemNode("foo", @"C:\Root\foo", true);
        var fooChild = new FileSystemNode("foo-child.txt", @"C:\Root\foo\foo-child.txt", false);
        var foobar = new FileSystemNode("foobar", @"C:\Root\foobar", true);
        var foobarChild = new FileSystemNode("foobar-child.txt", @"C:\Root\foobar\foobar-child.txt", false);
        var keep = new FileSystemNode("keep.txt", @"C:\Root\keep.txt", false);
        root.AddChild(foo);
        foo.AddChild(fooChild);
        root.AddChild(foobar);
        foobar.AddChild(foobarChild);
        root.AddChild(keep);

        return SearchState.Create([root, foo, fooChild, foobar, foobarChild, keep], Tokenizer);
    }

    private static bool IsTargetOrDescendant(string candidate, string target)
    {
        var normalizedTarget = target.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        return string.Equals(candidate, normalizedTarget, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(normalizedTarget + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
               candidate.StartsWith(normalizedTarget + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
