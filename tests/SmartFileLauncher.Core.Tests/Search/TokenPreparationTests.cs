using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using SmartFileLauncher.Core.Filtering;
using SmartFileLauncher.Core.Application.Search;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Models;
using SmartFileLauncher.Core.Search;
using SmartFileLauncher.Core.Search.Paged;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Search;

public sealed class TokenPreparationTests
{
    [Fact(Skip = "Token sistemi geçici olarak kapalı: TokenIndexing.Enabled=false")]
    public async Task AiSearchFallsBackWhenSnapshotBecomesIncompleteDuringIntentParsing()
    {
        using var readyFixture = new Fixture();
        using var pendingFixture = new Fixture();
        using var ready = readyFixture.Build(defer: false);
        using var pending = pendingFixture.Build();
        using var readyState = new PagedSearchState([(new CatalogStore(ready, false), false)]);
        using var pendingState = new PagedSearchState([(new CatalogStore(pending, false), false)]);
        var current = readyState;
        using var previousQuery = readyState.Acquire();
        Assert.True(previousQuery.IsTokenIndexReady);
        var tokenizer = new BasicTokenizer();
        var scoring = new BasicScoringStrategy();
        var advanced = new AdvancedSearchEngine(_ => current.Acquire(), tokenizer, scoring);
        var standard = new SearchEngine(_ => current.Acquire(), tokenizer, scoring, literalNamesOnly: true);
        var parsing = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var service = new SearchApplicationService(standard.Search, advanced.Search,
            async (_, _) => { parsing.SetResult(); await release.Task; return new StructuredQuery { Keywords = ["alpha"] }; },
            _ => new StructuredQuery());
        var search = service.SearchAsync(new("alpha", true, true));
        await parsing.Task;
        current = pendingState;
        release.SetResult();
        var result = await search;
        Assert.True(result.UsedFallback);
        Assert.Equal(SearchExecutionMode.Standard, result.Mode);
        Assert.Equal("alpha.txt", Assert.Single(result.Results).Name);
        Assert.Null(result.AutoOpenPath);
        using var incomplete = pendingState.Acquire();
        Assert.Throws<TokenIndexNotReadyException>(() => incomplete.Get("alpha"));
        Assert.Throws<TokenIndexNotReadyException>(() => incomplete.GetPartial("alph"));
        Assert.Throws<TokenIndexNotReadyException>(() => incomplete.GetFuzzy("alhpa"));
        Assert.Single(previousQuery.Get("alpha"));
    }

    [Fact(Skip = "Token sistemi geçici olarak kapalı: TokenIndexing.Enabled=false")]
    public async Task MetadataSearchPrecedesTokensAndPreparationResumesFromCommittedCursor()
    {
        using var fixture = new Fixture();
        using var original = fixture.Build();
        original.PrepareLiteralNameIndex(default);
        Assert.False(original.Data.TermsReady);
        Assert.Empty(new Reader(original).Get("alpha"));
        Assert.Single(original.SearchLiteralNames("alpha", 10, ResultView.SearchDefault, default));
        using (var writer = new CatalogStore(original, true))
        {
            Assert.True(await writer.PrepareTokenBatchAsync(default, 5));
            using var partial = writer.Acquire();
            Assert.False(partial.Data.TermsReady);
            Assert.Equal(5, partial.Data.TokenBuildCursor);
            Assert.Equal(42, partial.Data.Cursor);
            Assert.Equal("delivery-42", partial.Data.DeliveryId);
        }
        using var resumed = CatalogStore.Open(fixture.Directory, true);
        using (var partial = resumed.Acquire()) Assert.Equal(5, partial.Data.TokenBuildCursor);
        while (await resumed.PrepareTokenBatchAsync(default, 3)) { }
        using var complete = resumed.Acquire();
        Assert.True(complete.Data.TermsReady);
        Assert.Equal(complete.MaxId, complete.Data.TokenBuildCursor);
        Assert.Single(new Reader(complete).Get("alpha"));
        Assert.False(original.Data.TermsReady);
        Assert.Empty(new Reader(original).Get("alpha"));
        original.Data.ValidateAll();
        complete.Data.ValidateAll();
    }

    [Fact(Skip = "Token sistemi geçici olarak kapalı: TokenIndexing.Enabled=false")]
    public async Task RenameDeleteReuseAndDuplicatePostingsRemainCorrectAcrossBatches()
    {
        using var fixture = new Fixture();
        using var initial = fixture.Build();
        using var writer = new CatalogStore(initial, true);
        Assert.True(await writer.PrepareTokenBatchAsync(default, 7));
        using var before = writer.Acquire();
        var reusedId = before.Find(11);
        writer.Commit(catalog =>
        {
            catalog.Apply(new(10, 5, "alpha.txt", Entry(10, "common-alpha.txt")));
            catalog.Apply(new(11, 5, "beta.txt", null));
            catalog.Apply(new(20, 5, "replacement.txt", Entry(20, "replacement.txt")));
            catalog.Apply(new(14, 5, "epsilon.txt", Entry(14, "future.txt")));
            catalog.Apply(new(30, 5, "common-other.txt", Entry(30, "common-other.txt")));
            catalog.Apply(new(10, 5, "common-alpha.txt", Entry(10, "common-renamed.txt")));
        }, 99, "delivery-99");
        using (var changed = writer.Acquire()) Assert.Equal(reusedId, changed.Find(20));
        while (await writer.PrepareTokenBatchAsync(default, 3)) { }
        using var complete = writer.Acquire();
        using var reader = new Reader(complete);
        Assert.Empty(reader.Get("alpha"));
        Assert.Empty(reader.Get("beta"));
        Assert.Empty(reader.Get("epsilon"));
        Assert.Single(reader.Get("replacement"));
        Assert.Single(reader.Get("future"));
        Assert.Equal(2, reader.Get("common").Count);
        Assert.Equal(2, reader.GetPartial("comm").Count);
        Assert.Equal(2, reader.GetFuzzy("commom").Count);
        Assert.Equal(99, complete.Data.Cursor);
        Assert.Equal("delivery-99", complete.Data.DeliveryId);
        complete.Audit();
    }

    [Theory]
    [InlineData("pages")]
    [InlineData("frame")]
    public void FailedBatchKeepsPreviousDurableReadinessAndCursor(string fault)
    {
        using var fixture = new Fixture();
        using var baseline = fixture.Build();
        using (var draft = baseline.Fork())
        {
            draft.PrepareTokenBatch(3, default);
            Assert.Throws<IOException>(() => draft.Data.Commit(42, fault: fault));
        }
        using var reopened = new Catalog(Image.Open(fixture.Directory));
        Assert.Equal(baseline.Data.Frame, reopened.Data.Frame);
        Assert.Equal(0, reopened.Data.TokenBuildCursor);
        Assert.False(reopened.Data.TermsReady);
        reopened.Data.ValidateAll();
    }

    [Fact]
    public async Task CancellationDoesNotPublishAndTokenOnlyFramesReuseLiteralIndex()
    {
        using var fixture = new Fixture();
        using var initial = fixture.Build();
        initial.PrepareLiteralNameIndex(default);
        using var state = new PagedSearchState([(new CatalogStore(initial, false), false)]);
        using var writer = new CatalogStore(initial, true);
        using var cancelled = new CancellationTokenSource();
        cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => writer.PrepareTokenBatchAsync(cancelled.Token));
        using (var unchanged = writer.Acquire()) Assert.Equal(initial.Data.Frame, unchanged.Data.Frame);
        await writer.PrepareTokenBatchAsync(default, 4);
        using var next = writer.Acquire();
        using var reusedStore = state.Reuse(fixture.Directory, next.Data.Frame)!;
        using var reused = reusedStore.Acquire();
        var index = typeof(Catalog).GetField("_literalNameIndex", BindingFlags.NonPublic | BindingFlags.Instance)!;
        Assert.Same(index.GetValue(initial), index.GetValue(reused));
        foreach (var area in new[] { Image.Files, Image.Dirs, Image.Names, Image.Metadata })
        {
            Assert.Equal(initial.Data.Areas[area].Length, next.Data.Areas[area].Length);
            Assert.Equal(initial.Data.Areas[area].Pages.Select(p => (p.Segment.Path, p.Offset)),
                next.Data.Areas[area].Pages.Select(p => (p.Segment.Path, p.Offset)));
        }
        writer.Apply([new(10, 5, "alpha.txt", Entry(10, "alpha.txt") with { SizeBytes = 777 })], 43);
        using var changed = writer.Acquire();
        using var changedStore = state.Reuse(fixture.Directory, changed.Data.Frame)!;
        using var changedReader = changedStore.Acquire();
        Assert.Null(index.GetValue(changedReader));
        Assert.Single(initial.SearchLiteralNames("alpha", 10, ResultView.SearchDefault, default));
    }

    [Theory(Skip = "Token sistemi geçici olarak kapalı: TokenIndexing.Enabled=false")]
    [InlineData(4)]
    [InlineData(5)]
    [InlineData(6)]
    public void LegacyFramesRemainTokenReady(int version)
    {
        using var fixture = new Fixture();
        using var initial = fixture.Build(defer: false);
        var file = Path.Combine(fixture.Directory, initial.Data.Frame);
        var body = Image.ReadChecked(file);
        using var r = new BinaryReader(new MemoryStream(body));
        r.ReadInt32(); r.ReadInt32();
        var numbers = r.ReadBytes(64);
        var root = r.ReadString(); var volume = r.ReadString(); var delivery = r.ReadString();
        r.ReadBoolean(); r.ReadInt32();
        var tail = r.ReadBytes((int)(r.BaseStream.Length - r.BaseStream.Position));
        using var output = new MemoryStream();
        using (var w = new BinaryWriter(output, Encoding.UTF8, true))
        {
            w.Write(0x32474d49); w.Write(version); w.Write(numbers);
            w.Write(version == 4 ? new string(MemoryMarshal.Cast<byte, char>(Convert.FromBase64String(root))) : root);
            w.Write(volume); if (version >= 6) w.Write(delivery); w.Write(tail);
        }
        Image.WriteChecked(file, output.ToArray());
        using var old = new Catalog(Image.Open(fixture.Directory));
        Assert.True(old.Data.TermsReady);
        Assert.Equal(0, old.Data.TokenBuildCursor);
        Assert.Single(new Reader(old).Get("alpha"));
    }

    private static NtfsMftEntry Entry(ulong frn, string name, bool directory = false) =>
        new(frn, 5, name, directory, directory ? FileAttributes.Directory : FileAttributes.Normal,
            directory ? 0 : 123, DateTime.UnixEpoch.Ticks, DateTime.UnixEpoch.Ticks);

    private sealed class Fixture : IDisposable
    {
        internal string Directory { get; } = Path.Combine(Path.GetTempPath(), "OmniSpot.TokenBatch." + Guid.NewGuid().ToString("N"));
        internal Catalog Build(bool defer = true)
        {
            var catalog = Catalog.Create(Directory, 5, 1024);
            catalog.Data.TermsReady = !defer;
            catalog.Accept(Entry(5, "C:\\", true), addTerms: !defer);
            foreach (var pair in new[] { (10UL, "alpha.txt"), (11UL, "beta.txt"), (12UL, "gamma.txt"),
                (13UL, "delta.txt"), (14UL, "epsilon.txt") }) catalog.Accept(Entry(pair.Item1, pair.Item2), addTerms: !defer);
            catalog.Data.DeliveryId = "delivery-42";
            catalog.Finish(42, buildNameOrder: false);
            return catalog;
        }
        public void Dispose() { if (System.IO.Directory.Exists(Directory)) System.IO.Directory.Delete(Directory, true); }
    }
}
