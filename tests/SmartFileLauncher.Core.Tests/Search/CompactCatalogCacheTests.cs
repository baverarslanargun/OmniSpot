using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Search;

public sealed class CompactCatalogCacheTests
{
    [Fact]
    public void CheckpointPreservesDeltaMetadataTokensAndOldMappedReader()
    {
        using var workspace = new TemporaryDirectory();
        var database = workspace.CreateFile("index.db", "database version one");
        var root = workspace.CreateDirectory("root");
        var tokenizer = new BasicTokenizer();
        var folder = Item("root", root, directory: true, parent: "");
        var removed = Item("old.txt", Path.Combine(root, "old.txt"), parent: root);
        var changed = Item("original.txt", Path.Combine(root, "original.txt"), parent: root);
        var state = CompactSearchState.Create(new[] { folder, removed, changed }, tokenizer);
        var updated = changed with { Name = "görüşme-\ud800.txt", OpenCount = 17,
            CreatedTime = new DateTime(638000000000000001, DateTimeKind.Utc),
            LastWriteTime = new DateTime(638000000000000003, DateTimeKind.Local), SizeBytes = long.MaxValue };
        var added = Item("child\udfff.txt", Path.Combine(root, "child\udfff.txt"), parent: root);
        state = state.WithRecordChanges(new[] { removed.FullPath }, new[] { updated, added }, tokenizer);
        var transient = Item("temporary", Path.Combine(root, "temporary"), parent: root);
        state = state.WithRecordUpserts(new[] { transient }, tokenizer).WithoutPathAndDescendants(transient.FullPath);
        Assert.True(CompactCatalogCache.TrySave(database, new[] { root }, tokenizer, state, null, 0));
        Assert.True(CompactCatalogCache.TryLoad(database, new[] { root }, tokenizer, default, out var loaded));
        Assert.Equal(state.GetAllItems().OrderBy(item => item.FullPath), loaded!.State.GetAllItems().OrderBy(item => item.FullPath));
        Assert.Equal(state.Get("gorusme"), loaded.State.Get("gorusme"));
        Assert.Equal(state.GetChildren(root).OrderBy(item => item.FullPath), loaded.State.GetChildren(root).OrderBy(item => item.FullPath));
        Assert.Equal(state.Identity(added.FullPath), loaded.State.Identity(added.FullPath));
        Assert.Equal(state.DeltaCount, loaded.State.DeltaCount);
        var next = loaded.State.WithoutPathAndDescendants(updated.FullPath);
        Exception? failure = null;
        var saved = CompactCatalogCache.TrySave(database, new[] { root }, tokenizer, next, null, 0, error => failure = error);
        Assert.True(saved, failure?.ToString());
        Assert.True(CompactCatalogCache.TryLoad(database, new[] { root }, tokenizer, default, out var reloaded));
        Assert.False(reloaded!.State.ContainsPath(updated.FullPath));
        Assert.True(loaded.State.ContainsPath(updated.FullPath));
    }

    [Theory]
    [InlineData("database")]
    [InlineData("wal")]
    [InlineData("metadata")]
    [InlineData("catalog")]
    [InlineData("root")]
    public void ChangedOrIncompleteInputsRejectTheCheckpoint(string change)
    {
        using var workspace = new TemporaryDirectory();
        var database = workspace.CreateFile("index.db", "database version one");
        var root = workspace.CreateDirectory("root");
        var tokenizer = new BasicTokenizer();
        var state = CompactSearchState.Create(new[] { Item("root", root, true, "") }, tokenizer);
        Assert.True(CompactCatalogCache.TrySave(database, new[] { root }, tokenizer, state, null, 0));
        var roots = new[] { root };
        switch (change)
        {
            case "database": File.AppendAllText(database, "changed"); break;
            case "wal": File.WriteAllBytes(database + "-wal", new byte[32]); break;
            case "metadata": File.WriteAllText(database + ".catalog.meta", "broken"); break;
            case "catalog":
                var catalogPath = Directory.GetFiles(workspace.Path, "index.db.catalog.*.bin").Single();
                var bytes = File.ReadAllBytes(catalogPath);
                bytes[60] ^= 1;
                File.WriteAllBytes(catalogPath, bytes);
                break;
            case "root": roots = new[] { workspace.CreateDirectory("different") }; break;
        }
        Assert.False(CompactCatalogCache.TryLoad(database, roots, tokenizer, default, out _));
    }

    private static SearchItem Item(string name, string path, bool directory = false, string? parent = null) =>
        new(name, path, directory, null, null, null, 0, parent);
}
