using System.Collections.Concurrent;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class ThumbnailViewportSchedulerTests
{
    private const int ThumbnailSize = 128;
    private const int BatchSize = 10;

    [Theory]
    [InlineData(200, 0, 20, 1, 0, 40)]
    [InlineData(200, 100, 20, 1, 80, 140)]
    [InlineData(200, 190, 10, 1, 180, 200)]
    [InlineData(200, 100, 20, 3, 40, 180)]
    [InlineData(200, 100, 20, 0, 100, 120)]
    public void Window_CoversVisibleRangePlusRequestedScreens(
        int itemCount,
        int firstVisible,
        int visibleCount,
        int screens,
        int expectedStart,
        int expectedEnd)
    {
        var window = ThumbnailViewportScheduler.Window(
            itemCount,
            new ThumbnailViewport(firstVisible, visibleCount),
            screens);

        Assert.Equal(expectedStart, window.Start);
        Assert.Equal(expectedEnd, window.End);
    }

    [Fact]
    public void Window_IsEmptyWhenViewportIsNotMeasurable()
    {
        Assert.Equal((0, 0), ThumbnailViewportScheduler.Window(200, new ThumbnailViewport(0, 0), 1));
        Assert.Equal((0, 0), ThumbnailViewportScheduler.Window(0, new ThumbnailViewport(0, 20), 1));
    }

    [Fact]
    public async Task Update_RequestsOnlyTheVisibleWindow()
    {
        var thumbnails = new RecordingThumbnailService();
        var items = CreateItems(200);
        var scheduler = CreateScheduler(thumbnails);
        scheduler.Reset(items);

        scheduler.Update(new ThumbnailViewport(0, 20));
        await scheduler.Current;

        Assert.Equal(40, thumbnails.Requests.Count);
        Assert.Equal(40, items.Count(i => i.Thumbnail != null));
        Assert.All(items.Take(40), i => Assert.NotNull(i.Thumbnail));
        Assert.All(items.Skip(40), i => Assert.Null(i.Thumbnail));
    }

    [Fact]
    public async Task Update_DoesNoWorkWhenViewportIsNotMeasurable()
    {
        var thumbnails = new RecordingThumbnailService();
        var items = CreateItems(200);
        var scheduler = CreateScheduler(thumbnails);
        scheduler.Reset(items);

        scheduler.Update(new ThumbnailViewport(0, 0));
        await scheduler.Current;

        Assert.Empty(thumbnails.Requests);
        Assert.All(items, i => Assert.Null(i.Thumbnail));
    }

    [Fact]
    public async Task Update_DoesNotReleaseWhenViewportIsNotMeasurable()
    {
        var thumbnails = new RecordingThumbnailService();
        var items = CreateItems(60);
        var scheduler = CreateScheduler(thumbnails);
        scheduler.Reset(items);

        scheduler.Update(new ThumbnailViewport(0, 20));
        await scheduler.Current;
        var loaded = items.Count(i => i.Thumbnail != null);
        Assert.True(loaded > 0);

        scheduler.Update(new ThumbnailViewport(0, 0));
        await scheduler.Current;

        Assert.Equal(loaded, items.Count(i => i.Thumbnail != null));
        Assert.Equal(0, scheduler.ReleasedCount);
    }

    [Fact]
    public async Task Update_NeverReleasesWhileScrolling()
    {
        var thumbnails = new RecordingThumbnailService();
        var items = CreateItems(400);
        var scheduler = CreateScheduler(thumbnails);
        scheduler.Reset(items);

        scheduler.Update(new ThumbnailViewport(0, 20));
        await scheduler.Current;
        Assert.All(items.Take(40), i => Assert.NotNull(i.Thumbnail));

        scheduler.Update(new ThumbnailViewport(300, 20));
        await scheduler.Current;
        scheduler.Update(new ThumbnailViewport(0, 20));
        await scheduler.Current;

        Assert.Equal(0, scheduler.ReleasedCount);
        Assert.All(items.Take(40), i => Assert.NotNull(i.Thumbnail));
        Assert.All(items.Skip(280).Take(40), i => Assert.NotNull(i.Thumbnail));
    }

    [Fact]
    public async Task ReleaseOutsideViewport_KeepsTheLastWindowAndDropsTheRest()
    {
        var thumbnails = new RecordingThumbnailService();
        var items = CreateItems(400);
        var scheduler = CreateScheduler(thumbnails);
        scheduler.Reset(items);

        scheduler.Update(new ThumbnailViewport(0, 20));
        await scheduler.Current;
        scheduler.Update(new ThumbnailViewport(200, 20));
        await scheduler.Current;
        var loaded = items.Count(i => i.Thumbnail != null);
        Assert.True(loaded > 60);

        scheduler.ReleaseOutsideViewport();

        Assert.All(items.Skip(180).Take(60), i => Assert.NotNull(i.Thumbnail));
        Assert.All(items.Take(180), i => Assert.Null(i.Thumbnail));
        Assert.All(items.Skip(240), i => Assert.Null(i.Thumbnail));
        Assert.Equal(loaded - 60, scheduler.ReleasedCount);
    }

    [Fact]
    public async Task ReleaseOutsideViewport_LetsDroppedItemsLoadAgain()
    {
        var thumbnails = new RecordingThumbnailService();
        var items = CreateItems(200);
        var scheduler = CreateScheduler(thumbnails);
        scheduler.Reset(items);

        scheduler.Update(new ThumbnailViewport(0, 20));
        await scheduler.Current;
        scheduler.Update(new ThumbnailViewport(100, 20));
        await scheduler.Current;
        scheduler.ReleaseOutsideViewport();
        Assert.All(items.Take(40), i => Assert.Null(i.Thumbnail));

        scheduler.Update(new ThumbnailViewport(0, 20));
        await scheduler.Current;

        Assert.All(items.Take(40), i => Assert.NotNull(i.Thumbnail));
    }

    [Fact]
    public void ReleaseOutsideViewport_IsSafeBeforeAnyViewportIsKnown()
    {
        var scheduler = CreateScheduler(new RecordingThumbnailService());
        scheduler.Reset(CreateItems(50));

        scheduler.ReleaseOutsideViewport();

        Assert.Equal(0, scheduler.ReleasedCount);
    }

    [Fact]
    public async Task Update_DoesNotRequestAlreadyLoadedItemsAgain()
    {
        var thumbnails = new RecordingThumbnailService();
        var items = CreateItems(200);
        var scheduler = CreateScheduler(thumbnails);
        scheduler.Reset(items);

        scheduler.Update(new ThumbnailViewport(0, 20));
        await scheduler.Current;
        Assert.Equal(40, thumbnails.Requests.Count);

        scheduler.Update(new ThumbnailViewport(20, 20));
        await scheduler.Current;

        Assert.Equal(60, thumbnails.Requests.Count);
        Assert.Equal(60, items.Count(i => i.Thumbnail != null));
    }

    [Fact]
    public async Task Reset_CancelsTheAbandonedViewsWork()
    {
        var gate = new TaskCompletionSource();
        var thumbnails = new RecordingThumbnailService { Gate = gate.Task };
        var oldItems = CreateItems(200);
        var newItems = CreateItems(20, "yeni");
        var scheduler = CreateScheduler(thumbnails);

        scheduler.Reset(oldItems);
        scheduler.Update(new ThumbnailViewport(0, 20));

        scheduler.Reset(newItems);
        gate.SetResult();
        await scheduler.Current;

        Assert.All(oldItems, i => Assert.Null(i.Thumbnail));
    }

    [Fact]
    public async Task Update_DoesNotRetryItemsWhoseThumbnailCannotBeProduced()
    {
        var thumbnails = new RecordingThumbnailService { ReturnsNull = true };
        var items = CreateItems(20);
        var scheduler = CreateScheduler(thumbnails);
        scheduler.Reset(items);

        for (var round = 0; round < 5; round++)
        {
            scheduler.Update(new ThumbnailViewport(0, 20));
            await scheduler.Current;
        }

        Assert.Equal(20, thumbnails.Requests.Count);
        Assert.All(items, i => Assert.Null(i.Thumbnail));
    }

    [Fact]
    public async Task Update_ReloadsItemsLeftUnfinishedByACancelledRound()
    {
        var gate = new TaskCompletionSource();
        var thumbnails = new RecordingThumbnailService { Gate = gate.Task };
        var items = CreateItems(60);
        var scheduler = CreateScheduler(thumbnails);
        scheduler.Reset(items);

        scheduler.Update(new ThumbnailViewport(0, 20));
        var abandoned = scheduler.Current;

        gate.SetResult();
        scheduler.Update(new ThumbnailViewport(10, 20));
        await abandoned;
        await scheduler.Current;

        Assert.All(items.Take(50), i => Assert.NotNull(i.Thumbnail));
    }

    [Fact]
    public void Constructor_RejectsNegativePrefetch()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ThumbnailViewportScheduler(
            new RecordingThumbnailService(),
            (_, _) => Task.CompletedTask,
            ThumbnailSize,
            BatchSize,
            prefetchScreens: -1));
    }

    private static ThumbnailViewportScheduler CreateScheduler(
        IThumbnailService thumbnails,
        int prefetchScreens = 1)
        => new(
            thumbnails,
            (icon, image) =>
            {
                icon.Thumbnail = image;
                return Task.CompletedTask;
            },
            ThumbnailSize,
            BatchSize,
            prefetchScreens,
            batchDelayMilliseconds: 1,
            delayAsync: (_, _) => Task.CompletedTask);

    private static List<DesktopIconViewModel> CreateItems(int count, string prefix = "item")
        => Enumerable.Range(0, count)
            .Select(index => new DesktopIconViewModel
            {
                Name = $"{prefix}-{index}",
                FullPath = $"{prefix}-{index}"
            })
            .ToList();

    private sealed class RecordingThumbnailService : IThumbnailService
    {
        private static readonly ImageSource Image = CreateFrozenImage();

        public ConcurrentQueue<string> Requests { get; } = new();

        public Task? Gate { get; init; }

        public bool ReturnsNull { get; init; }

        public async Task<ImageSource?> GetThumbnailAsync(
            string path,
            int size,
            CancellationToken token = default)
        {
            Requests.Enqueue(path);
            if (Gate != null)
            {
                await Gate.ConfigureAwait(false);
            }

            return ReturnsNull ? null : Image;
        }

        public ThumbnailDiagnostics GetDiagnostics()
            => new(
                MemoryCacheCount: 0,
                MemoryCacheLimit: 0,
                MemoryCacheByteLimit: 0,
                Requests: Requests.Count,
                MemoryHits: 0,
                DiskHits: 0,
                ShellGenerated: 0,
                Failures: 0,
                LastDecodedPixelWidth: 0,
                LastDecodedPixelHeight: 0,
                DecodedBytes: 0,
                ActiveGenerations: 0,
                QueuedGenerations: 0,
                Evictions: 0,
                DiskCacheFileCount: 0,
                DiskCacheBytes: 0,
                DiskCacheMeasuredAt: null);

        public Task RefreshDiskCacheStatsAsync(CancellationToken token = default)
            => Task.CompletedTask;

        private static ImageSource CreateFrozenImage()
        {
            var image = BitmapSource.Create(
                1,
                1,
                96,
                96,
                PixelFormats.Bgra32,
                null,
                new byte[] { 0, 0, 0, 255 },
                4);
            image.Freeze();
            return image;
        }
    }
}
