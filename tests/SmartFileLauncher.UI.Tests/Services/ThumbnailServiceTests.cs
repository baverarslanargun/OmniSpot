using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using SmartFileLauncher.UI.Services;
using Xunit;

namespace SmartFileLauncher.UI.Tests.Services;

public sealed class ThumbnailServiceTests : IDisposable
{
    private const int RequestedSize = 128;

    private readonly string _root = Path.Combine(
        Path.GetTempPath(),
        "omnispot-thumbnail-tests",
        Guid.NewGuid().ToString("N"));

    public ThumbnailServiceTests()
    {
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try
        {
            Directory.Delete(_root, recursive: true);
        }
        catch (IOException)
        {
        }
        catch (UnauthorizedAccessException)
        {
        }
    }

    [Theory]
    [InlineData(256, 256, 128, 128)]
    [InlineData(256, 144, 128, 72)]
    [InlineData(144, 256, 72, 128)]
    [InlineData(1024, 512, 128, 64)]
    public void DecodeBounded_LimitsLongestEdgeToRequestedSize(
        int sourceWidth,
        int sourceHeight,
        int expectedWidth,
        int expectedHeight)
    {
        var png = CreatePng(sourceWidth, sourceHeight);

        var decoded = ThumbnailService.DecodeBounded(png, RequestedSize);

        Assert.NotNull(decoded);
        Assert.Equal(expectedWidth, decoded!.PixelWidth);
        Assert.Equal(expectedHeight, decoded.PixelHeight);
        Assert.True(decoded.IsFrozen);
    }

    [Fact]
    public void DecodeBounded_DoesNotUpscaleSourcesSmallerThanRequestedSize()
    {
        var png = CreatePng(48, 48);

        var decoded = ThumbnailService.DecodeBounded(png, RequestedSize);

        Assert.NotNull(decoded);
        Assert.Equal(48, decoded!.PixelWidth);
        Assert.Equal(48, decoded.PixelHeight);
    }

    [Fact]
    public void DecodeBounded_RetainsFarFewerPixelBytesThanAnUnboundedDecode()
    {
        var png = CreatePng(256, 256);

        var unbounded = ThumbnailService.DecodeBounded(png, 256);
        var bounded = ThumbnailService.DecodeBounded(png, RequestedSize);

        Assert.NotNull(unbounded);
        Assert.NotNull(bounded);

        var unboundedBytes = ThumbnailService.GetDecodedByteCount(unbounded!);
        var boundedBytes = ThumbnailService.GetDecodedByteCount(bounded!);

        Assert.Equal(256L * 256 * 4, unboundedBytes);
        Assert.Equal(128L * 128 * 4, boundedBytes);
        Assert.Equal(4, unboundedBytes / boundedBytes);
    }

    [Fact]
    public async Task GetThumbnailAsync_DiskCacheHitStaysWithinRequestedSize()
    {
        var cachePath = Path.Combine(_root, "cache");
        var service = CreateService(cachePath);
        var file = SeedDiskCacheEntry(cachePath, "big.png", 256, 256);

        var thumbnail = await service.GetThumbnailAsync(file, RequestedSize);

        Assert.NotNull(thumbnail);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(thumbnail);
        Assert.Equal(128, bitmap.PixelWidth);
        Assert.Equal(128, bitmap.PixelHeight);

        var diagnostics = service.GetDiagnostics();
        Assert.Equal(1, diagnostics.DiskHits);
        Assert.Equal(0, diagnostics.ShellGenerated);
        Assert.Equal(128L * 128 * 4, diagnostics.DecodedBytes);
    }

    [Fact]
    public async Task GetThumbnailAsync_SecondRequestIsServedFromMemory()
    {
        var cachePath = Path.Combine(_root, "cache");
        var service = CreateService(cachePath);
        var file = SeedDiskCacheEntry(cachePath, "repeat.png", 256, 256);

        var first = await service.GetThumbnailAsync(file, RequestedSize);
        var second = await service.GetThumbnailAsync(file, RequestedSize);

        Assert.NotNull(first);
        Assert.Same(first, second);

        var diagnostics = service.GetDiagnostics();
        Assert.Equal(1, diagnostics.DiskHits);
        Assert.Equal(1, diagnostics.MemoryHits);
        Assert.Equal(1, diagnostics.MemoryCacheCount);
    }

    [Fact]
    public async Task GetThumbnailAsync_MemoryCacheStaysWithinByteBudget()
    {
        var cachePath = Path.Combine(_root, "cache");
        var entryBytes = 128L * 128 * 4;
        var service = CreateService(cachePath, maxMemoryCacheBytes: entryBytes * 3);

        for (var index = 0; index < 12; index++)
        {
            var file = SeedDiskCacheEntry(cachePath, $"budget-{index}.png", 256, 256);
            Assert.NotNull(await service.GetThumbnailAsync(file, RequestedSize));
        }

        var diagnostics = service.GetDiagnostics();
        Assert.Equal(3, diagnostics.MemoryCacheCount);
        Assert.Equal(entryBytes * 3, diagnostics.DecodedBytes);
        Assert.True(diagnostics.DecodedBytes <= diagnostics.MemoryCacheByteLimit);
        Assert.Equal(9, diagnostics.Evictions);
    }

    [Fact]
    public async Task GetThumbnailAsync_EvictsLeastRecentlyUsedEntryFirst()
    {
        var cachePath = Path.Combine(_root, "cache");
        var entryBytes = 128L * 128 * 4;
        var service = CreateService(cachePath, maxMemoryCacheBytes: entryBytes * 2);

        var first = SeedDiskCacheEntry(cachePath, "lru-1.png", 256, 256);
        var second = SeedDiskCacheEntry(cachePath, "lru-2.png", 256, 256);
        var third = SeedDiskCacheEntry(cachePath, "lru-3.png", 256, 256);

        await service.GetThumbnailAsync(first, RequestedSize);
        await service.GetThumbnailAsync(second, RequestedSize);

        await service.GetThumbnailAsync(first, RequestedSize);
        await service.GetThumbnailAsync(third, RequestedSize);

        var before = service.GetDiagnostics();
        Assert.Equal(2, before.MemoryCacheCount);
        Assert.Equal(1, before.Evictions);

        await service.GetThumbnailAsync(first, RequestedSize);
        Assert.Equal(2, service.GetDiagnostics().MemoryHits);

        await service.GetThumbnailAsync(second, RequestedSize);
        Assert.Equal(2, service.GetDiagnostics().MemoryHits);
    }

    [Fact]
    public async Task GetThumbnailAsync_KeepsCountLimitAsSecondaryBound()
    {
        var cachePath = Path.Combine(_root, "cache");
        var service = CreateService(cachePath, maxMemoryCacheCount: 4);

        for (var index = 0; index < 10; index++)
        {
            var file = SeedDiskCacheEntry(cachePath, $"count-{index}.png", 32, 32);
            Assert.NotNull(await service.GetThumbnailAsync(file, RequestedSize));
        }

        var diagnostics = service.GetDiagnostics();
        Assert.Equal(4, diagnostics.MemoryCacheCount);
        Assert.Equal(4, diagnostics.MemoryCacheLimit);
        Assert.Equal(6, diagnostics.Evictions);
    }

    [Fact]
    public async Task GetThumbnailAsync_ShellGenerationStaysWithinRequestedSize()
    {
        var cachePath = Path.Combine(_root, "cache-shell");
        var service = CreateService(cachePath);
        var imagePath = Path.Combine(_root, "shell-source.png");
        File.WriteAllBytes(imagePath, CreatePng(1024, 768));

        var thumbnail = await service.GetThumbnailAsync(imagePath, RequestedSize);

        Assert.NotNull(thumbnail);
        var bitmap = Assert.IsAssignableFrom<BitmapSource>(thumbnail);
        Assert.True(
            Math.Max(bitmap.PixelWidth, bitmap.PixelHeight) <= RequestedSize,
            $"kabuk {bitmap.PixelWidth}×{bitmap.PixelHeight} döndürdü; sınır {RequestedSize}");

        var diagnostics = service.GetDiagnostics();
        Assert.Equal(1, diagnostics.ShellGenerated);
        Assert.Equal(0, diagnostics.Failures);
        Assert.True(diagnostics.DecodedBytes <= (long)RequestedSize * RequestedSize * 4);

        var key = new ThumbnailKey(
            imagePath,
            RequestedSize,
            new FileInfo(imagePath).LastWriteTimeUtc.Ticks);
        Assert.True(File.Exists(Path.Combine(cachePath, key.GetCacheFileName())));
    }

    [Fact]
    public async Task GetThumbnailAsync_ReportsFailureForMissingPath()
    {
        var service = CreateService(Path.Combine(_root, "cache"));

        var thumbnail = await service.GetThumbnailAsync(
            Path.Combine(_root, "yok.png"),
            RequestedSize);

        Assert.Null(thumbnail);
        Assert.Equal(1, service.GetDiagnostics().Failures);
    }

    private ThumbnailService CreateService(
        string cachePath,
        int maxMemoryCacheCount = ThumbnailService.DefaultMaxMemoryCacheCount,
        long maxMemoryCacheBytes = ThumbnailService.DefaultMaxMemoryCacheBytes)
        => new(_ => { }, cachePath, maxMemoryCacheCount, maxMemoryCacheBytes);

    private string SeedDiskCacheEntry(
        string cachePath,
        string fileName,
        int width,
        int height)
    {
        Directory.CreateDirectory(cachePath);
        var sourcePath = Path.Combine(_root, fileName);
        File.WriteAllBytes(sourcePath, CreatePng(width, height));

        var key = new ThumbnailKey(
            sourcePath,
            RequestedSize,
            new FileInfo(sourcePath).LastWriteTimeUtc.Ticks);
        File.WriteAllBytes(
            Path.Combine(cachePath, key.GetCacheFileName()),
            CreatePng(width, height));

        return sourcePath;
    }

    private static byte[] CreatePng(int width, int height)
    {
        var stride = width * 4;
        var pixels = new byte[stride * height];
        for (var index = 0; index < pixels.Length; index += 4)
        {
            pixels[index] = (byte)(index % 251);
            pixels[index + 1] = (byte)(index % 241);
            pixels[index + 2] = (byte)(index % 239);
            pixels[index + 3] = 255;
        }

        var source = BitmapSource.Create(
            width,
            height,
            96,
            96,
            PixelFormats.Bgra32,
            null,
            pixels,
            stride);

        using var buffer = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(source));
        encoder.Save(buffer);
        return buffer.ToArray();
    }
}
