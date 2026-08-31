using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.WindowsAPICodePack.Shell;

namespace SmartFileLauncher.UI.Services;

public class ThumbnailService : IThumbnailService
{
    internal const int DefaultMaxMemoryCacheCount = 1000;
    internal const long DefaultMaxMemoryCacheBytes = 64L * 1024 * 1024;

    private sealed record CacheEntry(ThumbnailKey Key, ImageSource Image, long Bytes);

    private readonly Dictionary<ThumbnailKey, LinkedListNode<CacheEntry>> _memoryCache = new();
    private readonly LinkedList<CacheEntry> _recency = new();
    private readonly object _memoryCacheLock = new();
    private readonly SemaphoreSlim _semaphore = new(4);
    private readonly int _maxMemoryCacheCount;
    private readonly long _maxMemoryCacheBytes;
    private readonly string _diskCachePath;
    private readonly Action<string> _log;

    private long _memoryCacheBytes;
    private long _requests;
    private long _memoryHits;
    private long _diskHits;
    private long _shellGenerated;
    private long _failures;
    private long _evictions;
    private int _lastDecodedPixelWidth;
    private int _lastDecodedPixelHeight;
    private int _activeGenerations;
    private int _queuedGenerations;
    private int _diskCacheScanGate;
    private DiskCacheStats? _diskCacheStats;

    private sealed record DiskCacheStats(int FileCount, long Bytes, DateTime MeasuredAt);

    public ThumbnailService(
        Action<string> log,
        string? diskCachePath = null,
        int maxMemoryCacheCount = DefaultMaxMemoryCacheCount,
        long maxMemoryCacheBytes = DefaultMaxMemoryCacheBytes)
    {
        _log = log;
        if (diskCachePath != null && string.IsNullOrWhiteSpace(diskCachePath))
        {
            throw new ArgumentException(
                "Thumbnail cache yolu boş olamaz.",
                nameof(diskCachePath));
        }

        if (maxMemoryCacheCount <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMemoryCacheCount),
                "Bellek önbelleği adet sınırı pozitif olmalı.");
        }

        if (maxMemoryCacheBytes <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxMemoryCacheBytes),
                "Bellek önbelleği bayt sınırı pozitif olmalı.");
        }

        _maxMemoryCacheCount = maxMemoryCacheCount;
        _maxMemoryCacheBytes = maxMemoryCacheBytes;

        _diskCachePath = diskCachePath switch
        {
            null => Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                "OmniSpot",
                "thumbcache"),
            _ => Path.GetFullPath(diskCachePath)
        };

        Directory.CreateDirectory(_diskCachePath);
        _log($"📁 Thumbnail cache: {_diskCachePath}");
    }

    public async Task<ImageSource?> GetThumbnailAsync(
        string path,
        int size,
        CancellationToken token = default)
    {
        Interlocked.Increment(ref _requests);
        try
        {
            if (size <= 0)
            {
                Interlocked.Increment(ref _failures);
                return null;
            }

            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Interlocked.Increment(ref _failures);
                return null;
            }

            var fileInfo = new FileInfo(path);
            var key = new ThumbnailKey(path, size, fileInfo.LastWriteTimeUtc.Ticks);

            if (TryGetFromMemoryCache(key, out var cachedImage))
            {
                Interlocked.Increment(ref _memoryHits);
                return cachedImage;
            }

            var diskCachePath = Path.Combine(_diskCachePath, key.GetCacheFileName());
            if (File.Exists(diskCachePath))
            {
                try
                {
                    var diskImage = LoadFromDiskCache(diskCachePath, size);
                    if (diskImage != null)
                    {
                        Interlocked.Increment(ref _diskHits);
                        AddToMemoryCache(key, diskImage);
                        return diskImage;
                    }
                }
                catch
                {
                }
            }

            Interlocked.Increment(ref _queuedGenerations);
            try
            {
                await _semaphore.WaitAsync(token);
            }
            finally
            {
                Interlocked.Decrement(ref _queuedGenerations);
            }

            Interlocked.Increment(ref _activeGenerations);
            try
            {
                if (TryGetFromMemoryCache(key, out cachedImage))
                {
                    return cachedImage;
                }

                return await Task.Run(() =>
                {
                    try
                    {
                        var thumbnail = GenerateShellThumbnail(path, size);
                        if (thumbnail == null)
                        {
                            Interlocked.Increment(ref _failures);
                            return null;
                        }

                        if (!thumbnail.IsFrozen)
                        {
                            thumbnail.Freeze();
                        }

                        var bounded = StoreAndBound(diskCachePath, thumbnail, size);
                        Interlocked.Increment(ref _shellGenerated);
                        AddToMemoryCache(key, bounded);
                        return bounded;
                    }
                    catch
                    {
                        Interlocked.Increment(ref _failures);
                        return null;
                    }
                }, token);
            }
            finally
            {
                Interlocked.Decrement(ref _activeGenerations);
                _semaphore.Release();
            }
        }
        catch
        {
            return null;
        }
    }

    public async Task RefreshDiskCacheStatsAsync(CancellationToken token = default)
    {
        if (Interlocked.CompareExchange(ref _diskCacheScanGate, 1, 0) != 0) return;

        try
        {
            var stats = await Task.Run(() => ScanDiskCache(token), token).ConfigureAwait(false);
            if (stats != null)
            {
                Volatile.Write(ref _diskCacheStats, stats);
            }
        }
        finally
        {
            Interlocked.Exchange(ref _diskCacheScanGate, 0);
        }
    }

    private DiskCacheStats? ScanDiskCache(CancellationToken token)
    {
        var count = 0;
        var bytes = 0L;

        try
        {
            foreach (var file in Directory.EnumerateFiles(_diskCachePath))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    bytes += new FileInfo(file).Length;
                    count++;
                }
                catch (IOException)
                {
                }
                catch (UnauthorizedAccessException)
                {
                }
            }
        }
        catch (DirectoryNotFoundException)
        {
            return null;
        }

        return new DiskCacheStats(count, bytes, DateTime.Now);
    }

    private BitmapSource? GenerateShellThumbnail(string path, int size)
    {
        try
        {
            using var shellFile = ShellFile.FromFilePath(path);

            var shellThumbnail = shellFile.Thumbnail;
            shellThumbnail.AllowBiggerSize = false;
            shellThumbnail.CurrentSize = new System.Windows.Size(size, size);

            var bitmapSource = shellThumbnail.BitmapSource;

            if (bitmapSource == null)
            {
                return null;
            }

            if (!bitmapSource.IsFrozen)
                bitmapSource.Freeze();

            return bitmapSource;
        }
        catch
        {
            return null;
        }
    }

    private BitmapSource StoreAndBound(string diskCachePath, BitmapSource generated, int size)
    {
        if (!ExceedsBound(generated.PixelWidth, generated.PixelHeight, size))
        {
            SaveToDiskCache(diskCachePath, generated);
            return generated;
        }

        byte[] encoded;
        try
        {
            encoded = EncodePng(generated);
        }
        catch
        {
            SaveToDiskCache(diskCachePath, generated);
            return generated;
        }

        try
        {
            File.WriteAllBytes(diskCachePath, encoded);
        }
        catch
        {
        }

        return DecodeBounded(encoded, size) ?? generated;
    }

    internal static bool ExceedsBound(int width, int height, int size)
        => Math.Max(width, height) > size;

    private static byte[] EncodePng(BitmapSource image)
    {
        using var buffer = new MemoryStream();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(image));
        encoder.Save(buffer);
        return buffer.ToArray();
    }

    internal static BitmapSource? DecodeBounded(byte[] data, int size)
    {
        if (data.Length == 0 || size <= 0)
        {
            return null;
        }

        int sourceWidth;
        int sourceHeight;
        using (var probe = new MemoryStream(data, writable: false))
        {
            var frame = BitmapFrame.Create(
                probe,
                BitmapCreateOptions.DelayCreation,
                BitmapCacheOption.None);
            sourceWidth = frame.PixelWidth;
            sourceHeight = frame.PixelHeight;
        }

        if (sourceWidth <= 0 || sourceHeight <= 0)
        {
            return null;
        }

        using var source = new MemoryStream(data, writable: false);
        var bitmap = new BitmapImage();
        bitmap.BeginInit();
        bitmap.CacheOption = BitmapCacheOption.OnLoad;
        bitmap.StreamSource = source;
        if (ExceedsBound(sourceWidth, sourceHeight, size))
        {
            if (sourceWidth >= sourceHeight)
            {
                bitmap.DecodePixelWidth = size;
            }
            else
            {
                bitmap.DecodePixelHeight = size;
            }
        }

        bitmap.EndInit();
        bitmap.Freeze();
        return bitmap;
    }

    internal static long GetDecodedByteCount(ImageSource image)
        => image is BitmapSource bitmap
            ? (long)bitmap.PixelWidth * bitmap.PixelHeight * bitmap.Format.BitsPerPixel / 8
            : 0L;

    private bool TryGetFromMemoryCache(ThumbnailKey key, out ImageSource? image)
    {
        lock (_memoryCacheLock)
        {
            if (_memoryCache.TryGetValue(key, out var node))
            {
                _recency.Remove(node);
                _recency.AddFirst(node);
                image = node.Value.Image;
                return true;
            }
        }

        image = null;
        return false;
    }

    private BitmapSource? LoadFromDiskCache(string cachePath, int size)
    {
        try
        {
            return DecodeBounded(File.ReadAllBytes(cachePath), size);
        }
        catch
        {
            return null;
        }
    }

    private void AddToMemoryCache(ThumbnailKey key, ImageSource image)
    {
        if (image is BitmapSource bitmap)
        {
            Volatile.Write(ref _lastDecodedPixelWidth, bitmap.PixelWidth);
            Volatile.Write(ref _lastDecodedPixelHeight, bitmap.PixelHeight);
        }

        var bytes = GetDecodedByteCount(image);

        lock (_memoryCacheLock)
        {
            if (_memoryCache.TryGetValue(key, out var existing))
            {
                _memoryCacheBytes -= existing.Value.Bytes;
                _recency.Remove(existing);
            }

            var node = _recency.AddFirst(new CacheEntry(key, image, bytes));
            _memoryCache[key] = node;
            _memoryCacheBytes += bytes;

            while (_recency.Count > 1
                && (_memoryCache.Count > _maxMemoryCacheCount
                    || _memoryCacheBytes > _maxMemoryCacheBytes))
            {
                var evicted = _recency.Last!;
                _recency.RemoveLast();
                _memoryCache.Remove(evicted.Value.Key);
                _memoryCacheBytes -= evicted.Value.Bytes;
                Interlocked.Increment(ref _evictions);
            }
        }
    }

    private void SaveToDiskCache(string cachePath, BitmapSource image)
    {
        try
        {
            using var fileStream = new FileStream(cachePath, FileMode.Create);
            var encoder = new PngBitmapEncoder();
            encoder.Frames.Add(BitmapFrame.Create(image));
            encoder.Save(fileStream);
        }
        catch
        {
        }
    }

    public void ClearMemoryCache()
    {
        lock (_memoryCacheLock)
        {
            var count = _memoryCache.Count;
            _memoryCache.Clear();
            _recency.Clear();
            _memoryCacheBytes = 0;
            _log($"🗑️ Memory cache cleared: {count} items");
        }
    }

    public void ClearDiskCache()
    {
        try
        {
            if (Directory.Exists(_diskCachePath))
            {
                var files = Directory.GetFiles(_diskCachePath);
                foreach (var file in files)
                {
                    File.Delete(file);
                }
                _log($"🗑️ Disk cache cleared: {files.Length} files");
            }
        }
        catch
        {
        }
    }

    public (int memoryCount, int maxMemory) GetCacheStats()
    {
        lock (_memoryCacheLock)
        {
            return (_memoryCache.Count, _maxMemoryCacheCount);
        }
    }

    public ThumbnailDiagnostics GetDiagnostics()
    {
        int count;
        long decodedBytes;
        lock (_memoryCacheLock)
        {
            count = _memoryCache.Count;
            decodedBytes = _memoryCacheBytes;
        }

        var diskCache = Volatile.Read(ref _diskCacheStats);

        return new ThumbnailDiagnostics(
            count,
            _maxMemoryCacheCount,
            _maxMemoryCacheBytes,
            Interlocked.Read(ref _requests),
            Interlocked.Read(ref _memoryHits),
            Interlocked.Read(ref _diskHits),
            Interlocked.Read(ref _shellGenerated),
            Interlocked.Read(ref _failures),
            Volatile.Read(ref _lastDecodedPixelWidth),
            Volatile.Read(ref _lastDecodedPixelHeight),
            decodedBytes,
            Volatile.Read(ref _activeGenerations),
            Volatile.Read(ref _queuedGenerations),
            Interlocked.Read(ref _evictions),
            diskCache?.FileCount ?? 0,
            diskCache?.Bytes ?? 0,
            diskCache?.MeasuredAt);
    }
}
