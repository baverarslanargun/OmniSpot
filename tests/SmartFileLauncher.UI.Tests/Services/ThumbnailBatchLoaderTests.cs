using System.Collections.Concurrent;
using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class ThumbnailBatchLoaderTests
{
    private const int ThumbnailSize = 128;
    private const int BatchSize = 10;

    [Fact]
    public async Task RunAsync_LoadsEveryItemWhenNotCancelled()
    {
        var thumbnails = new RecordingThumbnailService();
        var applied = new List<DesktopIconViewModel>();
        var items = CreateItems(25);
        var loader = CreateLoader(thumbnails, (icon, image) =>
        {
            icon.Thumbnail = image;
            applied.Add(icon);
            return Task.CompletedTask;
        });

        await loader.RunAsync(items, CancellationToken.None);

        Assert.Equal(25, thumbnails.Requests.Count);
        Assert.Equal(25, applied.Count);
        Assert.All(items, item => Assert.NotNull(item.Thumbnail));
        Assert.All(thumbnails.Sizes, size => Assert.Equal(ThumbnailSize, size));
    }

    /// <summary>
    /// Kök neden: klasör değiştiğinde eski tur `CancellationToken.None` ile
    /// `1.000` öğeye kadar devam ediyordu.
    /// </summary>
    [Fact]
    public async Task RunAsync_StopsOpeningNewRequestsAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var thumbnails = new RecordingThumbnailService();
        var items = CreateItems(500);
        var loader = CreateLoader(
            thumbnails,
            (icon, image) =>
            {
                icon.Thumbnail = image;
                return Task.CompletedTask;
            },
            delayAsync: (_, _) =>
            {
                // İlk batch bittikten hemen sonra kullanıcı başka klasöre geçiyor.
                cancellation.Cancel();
                return Task.CompletedTask;
            });

        await loader.RunAsync(items, cancellation.Token);

        Assert.Equal(BatchSize, thumbnails.Requests.Count);
        Assert.Equal(BatchSize, items.Count(item => item.Thumbnail != null));
    }

    /// <summary>
    /// İptal edilen tur, yeni UI durumuna küçük resim yazmamalı.
    /// </summary>
    [Fact]
    public async Task RunAsync_DoesNotApplyResultsProducedAfterCancellation()
    {
        using var cancellation = new CancellationTokenSource();
        var thumbnails = new RecordingThumbnailService();
        thumbnails.OnRequest = count =>
        {
            if (count == 1)
            {
                cancellation.Cancel();
            }
        };

        var items = CreateItems(BatchSize);
        var loader = CreateLoader(
            thumbnails,
            (icon, image) =>
            {
                icon.Thumbnail = image;
                return Task.CompletedTask;
            },
            delayAsync: (_, _) => Task.CompletedTask);

        await loader.RunAsync(items, cancellation.Token);

        // İlk istekte iptal geldi: aynı batch'teki kalan öğeler için istek açılmadı
        // ve gelen sonuç UI'ya yazılmadı.
        Assert.Single(thumbnails.Requests);
        Assert.DoesNotContain(items, item => item.Thumbnail != null);
    }

    [Fact]
    public async Task RunAsync_SkipsEverythingWhenAlreadyCancelled()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var thumbnails = new RecordingThumbnailService();

        var loader = CreateLoader(thumbnails, (_, _) => Task.CompletedTask);

        await loader.RunAsync(CreateItems(50), cancellation.Token);

        Assert.Empty(thumbnails.Requests);
    }

    [Fact]
    public async Task RunAsync_KeepsRunningWhenASingleItemFails()
    {
        var thumbnails = new RecordingThumbnailService
        {
            FailingPath = "item-4"
        };
        var items = CreateItems(12);
        var loader = CreateLoader(thumbnails, (icon, image) =>
        {
            icon.Thumbnail = image;
            return Task.CompletedTask;
        });

        await loader.RunAsync(items, CancellationToken.None);

        Assert.Equal(12, thumbnails.Requests.Count);
        Assert.Equal(11, items.Count(item => item.Thumbnail != null));
    }

    private static ThumbnailBatchLoader CreateLoader(
        IThumbnailService thumbnails,
        Func<DesktopIconViewModel, ImageSource, Task> applyAsync,
        Func<int, CancellationToken, Task>? delayAsync = null)
        => new(
            thumbnails,
            applyAsync,
            ThumbnailSize,
            BatchSize,
            batchDelayMilliseconds: 1,
            delayAsync: delayAsync ?? ((_, _) => Task.CompletedTask));

    private static List<DesktopIconViewModel> CreateItems(int count)
        => Enumerable.Range(0, count)
            .Select(index => new DesktopIconViewModel
            {
                Name = $"item-{index}",
                FullPath = $"item-{index}"
            })
            .ToList();

    private sealed class RecordingThumbnailService : IThumbnailService
    {
        private static readonly ImageSource Image = CreateFrozenImage();
        private int _requestCount;

        public ConcurrentQueue<string> Requests { get; } = new();

        public ConcurrentQueue<int> Sizes { get; } = new();

        public string? FailingPath { get; init; }

        public Action<int>? OnRequest { get; set; }

        public Task<ImageSource?> GetThumbnailAsync(
            string path,
            int size,
            CancellationToken token = default)
        {
            Requests.Enqueue(path);
            Sizes.Enqueue(size);
            OnRequest?.Invoke(Interlocked.Increment(ref _requestCount));

            if (string.Equals(path, FailingPath, StringComparison.Ordinal))
            {
                throw new IOException("küçük resim üretilemedi");
            }

            return Task.FromResult<ImageSource?>(Image);
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
