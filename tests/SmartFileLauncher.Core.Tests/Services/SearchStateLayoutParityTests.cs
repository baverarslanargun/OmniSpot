using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class SearchStateLayoutParityTests
{
    [Fact]
    public async Task BothLayoutsAgreeAcrossEveryPublishPath()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("kok");
        workspace.CreateDirectory(Path.Combine("kok", "belgeler"));
        workspace.CreateDirectory(Path.Combine("kok", "belgeler", "derin"));
        workspace.CreateFile(Path.Combine("kok", "rapor-2026.txt"));
        workspace.CreateFile(Path.Combine("kok", "belgeler", "rapor-ozet.pdf"));
        workspace.CreateFile(Path.Combine("kok", "belgeler", "derin", "İstanbul-notu.md"));
        workspace.CreateFile(Path.Combine("kok", "belgeler", "derin", "ırmak.txt"));

        await using var legacy = await Harness.CreateAsync(workspace, root, SearchStateLayout.Legacy);
        await using var compact = await Harness.CreateAsync(workspace, root, SearchStateLayout.Compact);

        Compare(legacy, compact, "ilk yayım");

        legacy.Open(Path.Combine(root, "rapor-2026.txt"));
        compact.Open(Path.Combine(root, "rapor-2026.txt"));
        Compare(legacy, compact, "açılma sayacı");

        var eklenen = workspace.CreateFile(Path.Combine("kok", "belgeler", "yeni-rapor.txt"));
        legacy.Apply(FileChangeType.Created, eklenen);
        compact.Apply(FileChangeType.Created, eklenen);
        Compare(legacy, compact, "dosya eklendi");

        var silinen = Path.Combine(root, "belgeler", "derin");
        Directory.Delete(silinen, recursive: true);
        legacy.Apply(FileChangeType.Deleted, silinen, isDirectory: true);
        compact.Apply(FileChangeType.Deleted, silinen, isDirectory: true);
        Compare(legacy, compact, "alt ağaç silindi");

        var yeniAd = Path.Combine(root, "rapor-2027.txt");
        File.Move(Path.Combine(root, "rapor-2026.txt"), yeniAd);
        legacy.Rename(Path.Combine(root, "rapor-2026.txt"), yeniAd);
        compact.Rename(Path.Combine(root, "rapor-2026.txt"), yeniAd);
        Compare(legacy, compact, "yeniden adlandırma");

        await legacy.ReconcileAsync(root);
        await compact.ReconcileAsync(root);
        Compare(legacy, compact, "uzlaştırma");
    }

    private static void Compare(Harness legacy, Harness compact, string stage)
    {
        var expected = legacy.Manager.CurrentSearchState;
        var actual = compact.Manager.CurrentSearchState;

        Assert.True(expected is SearchState, $"{stage}: legacy temsili değişti");
        Assert.True(actual is CompactSearchState, $"{stage}: kompakt temsil legacy'ye döndü");
        Assert.True(expected.ItemCount == actual.ItemCount,
            $"{stage}: öğe sayısı {expected.ItemCount} != {actual.ItemCount}");
        Assert.True(expected.TokenCount == actual.TokenCount,
            $"{stage}: token sayısı {expected.TokenCount} != {actual.TokenCount}");
        Assert.Equal(Ordered(expected.GetAllItems()), Ordered(actual.GetAllItems()));

        foreach (var token in new[] { "rapor", "ozet", "istanbul", "İSTANBUL", "ırmak", "notu", "yok" })
        {
            Assert.True(Ordered(expected.Get(token)).SequenceEqual(Ordered(actual.Get(token))),
                $"{stage}: Get({token}) ayrıştı");
            Assert.True(Ordered(expected.GetPartial(token)).SequenceEqual(Ordered(actual.GetPartial(token))),
                $"{stage}: GetPartial({token}) ayrıştı");
            Assert.True(Ordered(expected.GetFuzzy(token)).SequenceEqual(Ordered(actual.GetFuzzy(token))),
                $"{stage}: GetFuzzy({token}) ayrıştı");
        }

        foreach (var item in expected.GetAllItems().Where(item => item.IsDirectory))
        {
            var mirrored = actual.GetAllItems().Single(other => other.FullPath == item.FullPath);
            Assert.True(
                Ordered(expected.GetDescendants(item)).SequenceEqual(Ordered(actual.GetDescendants(mirrored))),
                $"{stage}: GetDescendants({item.Name}) ayrıştı");
            Assert.True(expected.ContainsPath(item.FullPath) == actual.ContainsPath(item.FullPath),
                $"{stage}: ContainsPath({item.Name}) ayrıştı");
        }
    }

    private static SearchItem[] Ordered(IReadOnlyCollection<SearchItem> items) =>
        items.OrderBy(item => item.FullPath, StringComparer.Ordinal).ToArray();

    private sealed class Harness : IAsyncDisposable
    {
        private readonly IndexDatabase _database;
        private readonly FileWatcherService _watcher;

        internal IndexManager Manager { get; }

        private Harness(IndexDatabase database, FileWatcherService watcher, IndexManager manager)
        {
            _database = database;
            _watcher = watcher;
            Manager = manager;
        }

        internal static async Task<Harness> CreateAsync(
            TemporaryDirectory workspace, string root, SearchStateLayout layout)
        {
            var database = new IndexDatabase(
                Path.Combine(workspace.Path, layout + "-index.db"));
            var watcher = new FileWatcherService(debounceMs: 1);
            var manager = new IndexManager(database, watcher, layout: layout);
            await manager.InitializeAsync(root);
            return new Harness(database, watcher, manager);
        }

        internal void Open(string path) => Manager.IncrementOpenCount(path);

        internal void Apply(FileChangeType type, string path, bool isDirectory = false) =>
            Assert.True(Manager.ApplyExternalChanges(
                [new() { ChangeType = type, FullPath = path, IsDirectory = isDirectory }]));

        internal void Rename(string oldPath, string newPath) =>
            Assert.True(Manager.ApplyExternalChanges(
                [new() { ChangeType = FileChangeType.Renamed, FullPath = newPath, OldPath = oldPath }]));

        internal Task ReconcileAsync(string root) =>
            Manager.ReconcileWithinLifecycleAsync(root);

        public async ValueTask DisposeAsync()
        {
            Manager.Dispose();
            _watcher.Dispose();
            _database.Dispose();
            await Task.CompletedTask;
        }
    }
}
