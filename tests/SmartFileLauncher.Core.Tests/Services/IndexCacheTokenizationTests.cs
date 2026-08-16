using System.Globalization;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Services;
using SmartFileLauncher.Core.Tests.TestInfrastructure;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Services;

/// <summary>
/// Kalıcı index dosya adlarını saklar, token'ları değil: önbellekten yüklerken
/// her ad **güncel** tokenizer ile yeniden token'lanır. Bu yüzden token üretim
/// kuralı değiştiğinde index'i silip yeniden taramak gerekmez — 310 bin öğelik
/// bir ağaçta bu, kullanıcıya bedelsiz bir tam tarama yüklerdi.
///
/// Test önce eski kuralla (yalnız `tr-TR` küçültme) bir önbellek kurar, sonra
/// güncel tokenizer ile yeniden açar. Tam tarama damgası (`LastFullScanTime`,
/// yalnız bootstrap'ta yazılır) değişmeden yeni yazımların bulunması gerekir.
/// Kalıcı index bir gün token saklamaya başlarsa bu test düşer.
/// </summary>
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

        // 1. koşum: eski kural. Aksansız/noktalı yazımlar bulunmamalı; bu,
        // 2. koşumdaki başarının gerçekten yeni kuraldan geldiğini kanıtlar.
        await RunAsync(databasePath, root, new LegacyTurkishTokenizer(), manager =>
        {
            var state = manager.CreateSearchState();

            Assert.Empty(state.Get("gorusme"));
            Assert.Empty(state.Get("istanbul"));
            Assert.Contains(state.Get("görüşme"), item => Same(item.FullPath, accented));
        });
        var afterBootstrap = ReadScanStamp(databasePath);
        Assert.NotNull(afterBootstrap);

        // 2. koşum: güncel kural, aynı veritabanı. Yeniden tarama yok.
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

    /// <summary>
    /// Normalizasyon öncesi `BasicTokenizer`: yalnız ayırıcılardan böler ve
    /// `tr-TR` ile küçültür. NFC toparlama ve katlama yoktur.
    /// </summary>
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
