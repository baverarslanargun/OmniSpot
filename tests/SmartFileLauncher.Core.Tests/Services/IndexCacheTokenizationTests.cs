using System.Globalization;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

public sealed class IndexCacheTokenizationTests
{
    [Fact]
    public async Task CacheReloadAppliesCurrentTokenizerWithoutFullRescan()
    {
        using var workspace = new TemporaryDirectory();
        var root = workspace.CreateDirectory("root");
        var accented = workspace.CreateFile(Path.Combine("root", "görüşme-notlari.txt"));
        var dottedI = workspace.CreateFile(Path.Combine("root", "ISTANBUL-RAPOR.txt"));
        var databasePath = Path.Combine(workspace.Path, "index.db");

        await RunAsync(databasePath, root, new LegacyTurkishTokenizer(), manager =>
        {
            var state = manager.CreateSearchState();

            Assert.Empty(state.Get("gorusme"));
            Assert.Empty(state.Get("istanbul"));
            Assert.Contains(state.Get("görüşme"), item => Same(item.FullPath, accented));
        });
        var afterBootstrap = ReadScanStamp(databasePath);
        Assert.NotNull(afterBootstrap);

        await RunAsync(databasePath, root, tokenizer: null, manager =>
        {
            var state = manager.CreateSearchState();

            Assert.Contains(state.Get("gorusme"), item => Same(item.FullPath, accented));
            Assert.Contains(state.Get("görüşme"), item => Same(item.FullPath, accented));
            Assert.Contains(state.Get("istanbul"), item => Same(item.FullPath, dottedI));
        });

        Assert.Equal(afterBootstrap, ReadScanStamp(databasePath));
    }

    private static bool Same(string left, string right) =>
        string.Equals(left, right, StringComparison.OrdinalIgnoreCase);

    private static async Task RunAsync(
        string databasePath,
        string root,
        ITokenizer? tokenizer,
        Action<IndexManager> assert)
    {
        var database = new IndexDatabase(databasePath);
        var watcher = new FileWatcherService(debounceMs: 1);
        var manager = new IndexManager(database, watcher, tokenizer);
        try
        {
            await manager.InitializeAsync(new[] { root });
            assert(manager);
        }
        finally
        {
            manager.Dispose();
        }
    }

    private static string? ReadScanStamp(string databasePath)
    {
        using var database = new IndexDatabase(databasePath);
        database.Open();
        return database.GetMetadata(IndexMetadata.Keys.LastFullScanTime);
    }

    private sealed class LegacyTurkishTokenizer : ITokenizer
    {
        private static readonly char[] Delimiters =
            [' ', '_', '-', '.', ',', '[', ']', '(', ')'];

        private readonly CultureInfo _culture = new("tr-TR");

        public IEnumerable<string> Tokenize(string input)
        {
            if (string.IsNullOrWhiteSpace(input))
            {
                yield break;
            }

            foreach (var part in input.Split(
                Delimiters,
                StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            {
                yield return part.ToLower(_culture);
            }
        }
    }
}
