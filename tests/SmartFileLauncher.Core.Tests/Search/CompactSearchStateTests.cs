using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class CompactSearchStateTests
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CompactionPreservesOldReadersAndRemovesStalePostings(bool varint)
    {
        var tokenizer = new BasicTokenizer();
        var nodes = Enumerable.Range(0, 12).Select(index => Node("shared-" + index)).ToArray();
        var state = CompactSearchState.Create(nodes, tokenizer, deltaCapacity: 4, varint: varint);
        var snapshots = new List<(CompactSearchState State, SearchItem[] Items)>();
        var stableIdentity = state.Identity(nodes[11].FullPath);
        for (var index = 0; index < 12; index++)
        {
            snapshots.Add((state, Ordered(state)));
            var replacement = new FileSystemNode("replacement-" + index, nodes[index].FullPath, false)
            {
                Metadata = new() { OpenCount = index, SizeBytes = index }
            };
            state = state.WithUpserts([replacement], tokenizer);
            if (index == 0) Assert.Equal(stableIdentity, state.Identity(nodes[11].FullPath));
            Assert.Equal(11 - index, state.Get("shared").Count);
            Assert.True(state.DeltaCount < 4);
        }
        Assert.True(state.Generation >= 3);
        Assert.Equal(12, state.Get("replacement").Count);
        foreach (var (old, items) in snapshots) Assert.Equal(items, Ordered(old));
        var beforeDelete = state;
        for (var index = 0; index < 12; index++) state = state.WithoutPathAndDescendants(nodes[index].FullPath);
        Assert.Equal(0, state.ItemCount);
        Assert.Equal(0, state.TokenCount);
        Assert.Equal(12, beforeDelete.ItemCount);
        Assert.True(state.DeltaCount < 4);
    }

    [Fact]
    public void OrphanRepairAndTypeReplacementMatchFullState()
    {
        var tokenizer = new BasicTokenizer();
        var parent = new FileSystemNode("parent", @"C:\Synthetic\parent", true);
        var child = new FileSystemNode("old", parent.FullPath + @"\old", false);
        parent.AddChild(child);
        var compact = CompactSearchState.Create([child], tokenizer);
        var legacy = SearchState.Create([child], tokenizer);
        compact = compact.WithUpserts([parent], tokenizer);
        legacy = legacy.WithUpserts([parent], tokenizer);
        Assert.Equal(Ordered(legacy), Ordered(compact));
        var file = new FileSystemNode("new", parent.FullPath, false);
        compact = compact.WithUpserts([file], tokenizer);
        legacy = legacy.WithUpserts([file], tokenizer);
        Assert.Equal(Ordered(legacy), Ordered(compact));
        Assert.Empty(compact.Get("old"));
        Assert.Equal(legacy.TokenCount, compact.TokenCount);
    }

    [Fact]
    public void MappedReaderSurvivesFileDeletionAndRejectsIncompleteCatalog()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "synthetic.catalog");
        var state = CompactSearchState.Create([Node("old")], new BasicTokenizer());
        state.WriteNewBase(path);
        var old = CompactSearchState.OpenMapped(path);
        File.Delete(path);
        Assert.Single(old.Get("old"));
        var broken = Path.Combine(workspace.Path, "broken.catalog");
        File.WriteAllBytes(broken, [1, 2, 3]);
        Assert.Throws<InvalidDataException>(() => CompactSearchState.OpenMapped(broken));
        var damaged = Path.Combine(workspace.Path, "damaged.catalog");
        state.WriteNewBase(damaged);
        var bytes = File.ReadAllBytes(damaged);
        bytes[60] ^= 1;
        File.WriteAllBytes(damaged, bytes);
        Assert.Throws<InvalidDataException>(() => CompactSearchState.OpenMapped(damaged));
    }

    [Fact]
    public void MetadataPrecisionAndUnpairedUtf16ArePreserved()
    {
        var parent = new FileSystemNode("ROOT", @"C:\Synthetic", true);
        var item = new FileSystemNode("unpaired\ud800", parent.FullPath + "\\unpaired\ud800", false)
        {
            Metadata = new()
            {
                SizeBytes = long.MaxValue, OpenCount = int.MaxValue,
                CreatedTime = new DateTime(638000000000000001L, DateTimeKind.Utc),
                LastWriteTime = new DateTime(638000000000000002L, DateTimeKind.Local)
            }
        };
        parent.AddChild(item);
        var expected = SearchState.Create([parent, item], new BasicTokenizer());
        var actual = CompactSearchState.Create([parent, item], new BasicTokenizer());
        Assert.Equal(Ordered(expected), Ordered(actual));
    }

    [Fact]
    public async Task ConcurrentQueriesAndMappedCompactionKeepTheirOwnGeneration()
    {
        using var workspace = new TemporaryDirectory();
        var tokenizer = new BasicTokenizer();
        var original = CompactSearchState.Create([Node("old"), Node("shared")], tokenizer, varint: true);
        var firstPath = Path.Combine(workspace.Path, "first.catalog");
        original.WriteNewBase(firstPath);
        var old = CompactSearchState.OpenMapped(firstPath);
        var updated = old.WithUpserts([Node("new")], tokenizer);
        var nextPath = Path.Combine(workspace.Path, "next.catalog");
        var merged = updated.CompactToNewMapped(nextPath);
        Assert.Equal(old.Generation + 1, merged.Generation);
        Assert.Empty(old.Get("new"));
        Assert.Equal(Ordered(updated), Ordered(merged));
        var reopened = CompactSearchState.OpenMapped(nextPath);
        Assert.Equal(Ordered(merged), Ordered(reopened));
        Assert.Equal(merged.Generation, reopened.Generation);
        File.Delete(firstPath);
        var engine = new SearchEngine(_ => (ISearchStateReader)old, tokenizer, new BasicScoringStrategy());
        await Task.WhenAll(Enumerable.Range(0, 16).Select(_ => Task.Run(() =>
        {
            Assert.Single(engine.Search("old"));
            Assert.Empty(engine.Search("new"));
        })));
        var newest = merged.WithUpserts([Node("latest")], tokenizer);
        Assert.Single(newest.Get("latest"));
        Assert.Empty(merged.Get("latest"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void MappedCompactionWritesTheSameCatalogAsTheBufferedPath(bool varint)
    {
        using var workspace = new TemporaryDirectory();
        var tokenizer = new BasicTokenizer();
        var basePath = Path.Combine(workspace.Path, "base.catalog");
        CompactSearchState.Create([Node("old"), Node("shared")], tokenizer, varint: varint).WriteNewBase(basePath);
        var updated = CompactSearchState.OpenMapped(basePath).WithUpserts([Node("new")], tokenizer);

        var streamedPath = Path.Combine(workspace.Path, "streamed.catalog");
        var streamed = updated.CompactToNewMapped(streamedPath);
        var bufferedPath = Path.Combine(workspace.Path, "buffered.catalog");
        updated.Compact().WriteNewBase(bufferedPath);

        Assert.Equal(File.ReadAllBytes(bufferedPath), File.ReadAllBytes(streamedPath));
        Assert.Equal(Ordered(updated), Ordered(streamed));
        Assert.Equal(updated.Generation + 1, streamed.Generation);
        Assert.ThrowsAny<IOException>(() =>
            new FileStream(streamedPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
        GC.KeepAlive(streamed);
    }

    [Fact]
    public void SyntheticRootsAreNavigableWithoutBeingMissingParents()
    {
        var tokenizer = new BasicTokenizer();
        var cRoot = new SearchItem("C:", @"C:\", true, null, null, null, 0, "");
        var dRoot = new SearchItem("D:", @"D:\", true, null, null, null, 0, "");
        var child = new SearchItem("child.txt", @"C:\child.txt", false, 7, null, null, 1, cRoot.FullPath);
        var state = CompactSearchState.Create(new[] { cRoot, dRoot, child }, tokenizer);

        Assert.Equal(0, state.MissingParentCount);
        Assert.Equal(new[] { cRoot.FullPath, dRoot.FullPath }, state.GetRoots()
            .Select(item => item.FullPath).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Equal(new[] { cRoot.FullPath, dRoot.FullPath }, state.GetChildren("")
            .Select(item => item.FullPath).OrderBy(path => path, StringComparer.Ordinal).ToArray());
        Assert.Equal(child, Assert.Single(state.GetChildren(cRoot.FullPath)));

        var eRoot = new SearchItem("E:", @"E:\", true, null, null, null, 0, "");
        state = state.WithRecordUpserts([eRoot], tokenizer);
        Assert.Equal(0, state.MissingParentCount);
        Assert.Contains(eRoot, state.GetRoots());
        Assert.Contains(eRoot, state.GetChildren(""));

        state = state.WithRecordChanges([eRoot.FullPath], Array.Empty<SearchItem>(), tokenizer);
        Assert.Equal(0, state.MissingParentCount);
        Assert.DoesNotContain(eRoot, state.GetRoots());

        state = state.WithoutPathAndDescendants(cRoot.FullPath);
        Assert.Equal(0, state.MissingParentCount);
        Assert.False(state.ContainsPath(cRoot.FullPath));
        Assert.False(state.ContainsPath(child.FullPath));
        Assert.True(state.ContainsPath(dRoot.FullPath));
    }

    [Fact]
    public void TrueOrphanStillUsesFallbackAndRepairsMissingParentCount()
    {
        var tokenizer = new BasicTokenizer();
        var root = new SearchItem("D:", @"D:\", true, null, null, null, 0, "");
        var missingParent = @"C:\missing";
        var orphan = new SearchItem("orphan", missingParent + @"\orphan", true,
            null, null, null, 0, missingParent);
        var grandchild = new SearchItem("leaf.txt", orphan.FullPath + @"\leaf.txt", false,
            3, null, null, 0, orphan.FullPath);
        var state = CompactSearchState.Create(new[] { root, orphan, grandchild }, tokenizer);

        Assert.Equal(1, state.MissingParentCount);
        Assert.Equal(orphan, Assert.Single(state.GetChildren(missingParent)));

        state = state.WithoutPathAndDescendants(missingParent);

        Assert.Equal(0, state.MissingParentCount);
        Assert.True(state.ContainsPath(root.FullPath));
        Assert.False(state.ContainsPath(orphan.FullPath));
        Assert.False(state.ContainsPath(grandchild.FullPath));
    }

    [Fact]
    public void RecordApiPreservesMetadataAndOldSnapshots()
    {
        var tokenizer = new BasicTokenizer();
        var root = new SearchItem("ROOT", @"C:\Synthetic", true, null, null, null, 0, "");
        var originalItem = new SearchItem(
            "old-record.txt",
            root.FullPath + @"\old-record.txt",
            false,
            long.MaxValue,
            new DateTime(638000000000000001L, DateTimeKind.Utc),
            new DateTime(638000000000000002L, DateTimeKind.Local),
            int.MaxValue,
            root.FullPath);
        IIndexCatalogSnapshot original = CompactSearchState.Create(
            new[] { root, originalItem }, tokenizer);

        Assert.True(original.TryGetItem(originalItem.FullPath, out var read));
        Assert.Equal(originalItem, read);
        Assert.Equal(638000000000000001L, read.CreatedTime!.Value.Ticks);
        Assert.Equal(DateTimeKind.Utc, read.CreatedTime.Value.Kind);
        Assert.Equal(638000000000000002L, read.LastWriteTime!.Value.Ticks);
        Assert.Equal(DateTimeKind.Local, read.LastWriteTime.Value.Kind);
        Assert.Equal(long.MaxValue, read.SizeBytes);
        Assert.Equal(int.MaxValue, read.OpenCount);
        Assert.Equal(root, Assert.Single(original.GetRoots()));
        Assert.Equal(originalItem, Assert.Single(original.GetChildren(root.FullPath)));

        var replacement = originalItem with
        {
            Name = "new-record.txt",
            SizeBytes = null,
            CreatedTime = new DateTime(638000000000000003L, DateTimeKind.Unspecified),
            LastWriteTime = null,
            OpenCount = 4
        };
        var updated = original.WithRecordUpserts([replacement], tokenizer);

        Assert.True(original.TryGetItem(originalItem.FullPath, out var oldRead));
        Assert.Equal(originalItem, oldRead);
        Assert.Single(original.Get("old"));
        Assert.True(updated.TryGetItem(replacement.FullPath, out var newRead));
        Assert.Equal(replacement, newRead);
        Assert.Equal(DateTimeKind.Unspecified, newRead.CreatedTime!.Value.Kind);
        Assert.Empty(updated.Get("old"));
        Assert.Single(updated.Get("new"));
        Assert.Equal(replacement, Assert.Single(updated.GetChildren(root.FullPath)));

        var removed = updated.WithRecordChanges(
            [replacement.FullPath], Array.Empty<SearchItem>(), tokenizer);
        Assert.False(removed.TryGetItem(replacement.FullPath, out _));
        Assert.True(updated.TryGetItem(replacement.FullPath, out _));
    }

    [Fact]
    public void CompactedStateDoesNotKeepUnreferencedMappedGenerationAlive()
    {
        using var workspace = new TemporaryDirectory();
        var path = Path.Combine(workspace.Path, "retired.catalog");
        var (current, previous) = RetireMappedGeneration(path);
        GC.Collect(); GC.WaitForPendingFinalizers(); GC.Collect();
        Assert.False(previous.TryGetTarget(out _));
        using var exclusive = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Single(current.Get("old"));
    }

    [System.Runtime.CompilerServices.MethodImpl(System.Runtime.CompilerServices.MethodImplOptions.NoInlining)]
    private static (CompactSearchState, WeakReference<CompactSearchState>) RetireMappedGeneration(string path)
    {
        var source = CompactSearchState.Create([Node("old")], new BasicTokenizer());
        source.WriteNewBase(path);
        var mapped = CompactSearchState.OpenMapped(path);
        return (mapped.Compact(), new(mapped));
    }

    private static FileSystemNode Node(string name) => new(name, @"C:\Synthetic\" + name, false);
    private static SearchItem[] Ordered(ISearchStateReader state) => state.GetAllItems()
        .OrderBy(item => item.FullPath, StringComparer.Ordinal).ToArray();
}
