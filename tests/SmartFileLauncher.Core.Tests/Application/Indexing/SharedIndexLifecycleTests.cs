using SmartFileLauncher.Core.Application.Indexing;
using SmartFileLauncher.Core.Application.Settings;
using SmartFileLauncher.Core.ChangeFeed.Ipc;
using SmartFileLauncher.Core.Indexing.Ntfs;
using SmartFileLauncher.Core.Search.Paged;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Indexing;

public sealed class SharedIndexLifecycleTests
{
    [Fact]
    public async Task OldServiceFailsBeforeOpeningOrConsumingRebuildRequest()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OmniSpot.OldCapabilities." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var marker = Path.Combine(directory, "rebuild.request");
            File.WriteAllText(marker, "pending");
            var channel = new Channel(_ => new(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok, Message: "continuous-usn-v1"));
            using var lifecycle = new SharedIndexLifecycleService(channel, new Locations(), IndexScanMethod.Mft, Path.Combine(directory, "control"));
            var failure = await Assert.ThrowsAsync<IndexIssueException>(() => lifecycle.InitializeAsync());
            Assert.Equal(IndexIssueReason.ServiceVersionMismatch, failure.Issue.Reason);
            Assert.Equal(new[] { ChangeFeedRequestKind.GetCapabilities }, channel.Requests);
            Assert.True(File.Exists(marker));
        }
        finally { Directory.Delete(directory, true); }
    }

    [Fact]
    public async Task BackgroundCatalogWarningPreservesSearchUntilTheServiceRecovers()
    {
        using var directory = new SmartFileLauncher.Core.Tests.TestInfrastructure.TemporaryDirectory();
        var options = new IndexBuildOptions().ForService();
        var issue = new IndexIssue(IndexIssueReason.JournalInactive, @"C:\", "FSCTL_QUERY_USN_JOURNAL", "Günlük etkin değil", 1179);
        var warn = 0;
        var channel = new Channel(request => request.Kind == ChangeFeedRequestKind.GetCapabilities
            ? new(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok,
                Capabilities: ["shared-build-options-v1", "two-phase-index-v1"])
            : new(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok,
                Catalog: new SharedCatalogStatus("session", "Hazır", 0, 0, true, 1, [], AppliedBuildOptions: options)
                { IndexIssue = Volatile.Read(ref warn) == 1 ? issue : null }));
        using var lifecycle = new SharedIndexLifecycleService(channel, new Locations(), IndexScanMethod.SelectedFolders,
            Path.Combine(directory.Path, "control"), options);
        var updates = System.Threading.Channels.Channel.CreateUnbounded<IndexHealth>();
        lifecycle.HealthChanged += health => updates.Writer.TryWrite(health);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        await lifecycle.InitializeAsync(deadline.Token);
        Assert.Empty((await updates.Reader.ReadAsync(deadline.Token)).Issues);

        Volatile.Write(ref warn, 1);
        var degraded = await updates.Reader.ReadAsync(deadline.Token);
        Assert.True(degraded.SearchAvailable);
        Assert.Equal(issue, Assert.Single(degraded.Issues));
        Volatile.Write(ref warn, 0);
        var recovered = await updates.Reader.ReadAsync(deadline.Token);
        Assert.True(recovered.SearchAvailable);
        Assert.Empty(recovered.Issues);
    }

    [Fact(Skip = "Token sistemi geçici olarak kapalı: TokenIndexing.Enabled=false")]
    public async Task OneUnfinishedPartKeepsAiUnavailableWhileMetadataSearchWorks()
    {
        var directory = Path.Combine(Path.GetTempPath(), "OmniSpot.TwoPhaseLifecycle." + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            using var first = Build(Path.Combine(directory, "a"), @"C:\a", "alpha.txt", false);
            using var second = Build(Path.Combine(directory, "b"), @"C:\b", "beta.txt", true);
            using var writer = new CatalogStore(second, true);
            var options = new IndexBuildOptions(IndexScanWorkerMode.Manual, 4, IndexSortMethod.Merge, false, 3);
            SharedCatalogStatus Status()
            {
                using var current = writer.Acquire();
                var catalogs = new[] { first, current };
                return new("session", "AI hazırlanıyor", 4, 1, true, current.Data.Generation,
                    catalogs.Select(c => new SharedCatalogPart(ChangeFeedDeliveryContract.EncodeScope(c.Data.RootPath),
                        c.Data.Directory, c.Data.Frame, c.Data.Generation, c.Data.EntryCount)).ToArray(),
                    Files: 2, Directories: 2, Tokens: catalogs.Sum(c => new Reader(c).TokenCount),
                    AcceptedOperation: 1, CompletedOperation: 1, AppliedBuildOptions: options,
                    TokenIndexReady: catalogs.All(c => c.Data.TermsReady));
            }
            var channel = new Channel(request => request.Kind == ChangeFeedRequestKind.GetCapabilities
                ? new(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok,
                    Capabilities: ["shared-build-options-v1", "two-phase-index-v1"])
                : new(ChangeFeedProtocol.Version, ChangeFeedResponseStatus.Ok, Catalog: Status()));
            using var lifecycle = new SharedIndexLifecycleService(channel, new Locations(), IndexScanMethod.Mft,
                Path.Combine(directory, "control"), options);
            var backgroundReady = false;
            lifecycle.ProgressChanged += progress => { if (progress.IsBackgroundPreparation && progress.Phase == "tokens-ready") backgroundReady = true; };
            await lifecycle.InitializeAsync();
            Assert.True(lifecycle.IsInitialized);
            Assert.False(lifecycle.IsTokenIndexReady);
            Assert.Equal(0, lifecycle.GetStats().TokenCount);
            var state = lifecycle.CreateSearchState();
            using var stateLease = state as IDisposable;
            Assert.True(state.ContainsPath(@"C:\b\beta.txt"));
            while (await writer.PrepareTokenBatchAsync(default)) { }
            Assert.True(await lifecycle.EnsureSyncedAsync(@"C:\b"));
            Assert.True(lifecycle.IsTokenIndexReady);
            Assert.True(backgroundReady);
            Assert.True(lifecycle.GetStats().TokenCount > 0);
        }
        finally { Directory.Delete(directory, true); }
    }

    private static Catalog Build(string path, string root, string name, bool deferred)
    {
        var catalog = Catalog.Create(path, 5, 1024, rootPath: root);
        catalog.Data.TermsReady = !deferred;
        catalog.Accept(new(5, 5, Path.GetFileName(root), true, FileAttributes.Directory, 0, DateTime.UnixEpoch.Ticks, DateTime.UnixEpoch.Ticks), addTerms: !deferred);
        catalog.Accept(new(6, 5, name, false, FileAttributes.Normal, 1, DateTime.UnixEpoch.Ticks, DateTime.UnixEpoch.Ticks), addTerms: !deferred);
        catalog.Finish(buildNameOrder: false);
        return catalog;
    }
    private sealed class Locations : IIndexedLocationProvider
    {
        public IndexLocations Resolve() => new(@"C:\a", [@"C:\a", @"C:\b"]);
    }
    private sealed class Channel(Func<ChangeFeedRequest, ChangeFeedResponse> reply) : IChangeFeedRequestChannel
    {
        internal List<ChangeFeedRequestKind> Requests { get; } = [];
        public Task<ChangeFeedResponse> SendAsync(ChangeFeedRequest request, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            Requests.Add(request.Kind);
            return Task.FromResult(reply(request));
        }
    }
}
