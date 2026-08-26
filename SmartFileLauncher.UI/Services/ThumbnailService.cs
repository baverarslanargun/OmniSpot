using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.WindowsAPICodePack.Shell;

namespace SmartFileLauncher.UI.Services;

/// <summary>
/// Professional thumbnail service with memory + disk cache and Windows Shell integration.
/// OmniSpot: Hafif Basit Masaüstü ve Tarayıcı
/// </summary>
public class ThumbnailService : IThumbnailService
{
    private readonly Dictionary<ThumbnailKey, ImageSource> _memoryCache = new();
    private readonly SemaphoreSlim _semaphore = new(4); // Max 4 concurrent thumbnail generations
    private readonly int _maxMemoryCacheCount = 1000;
    private readonly string _diskCachePath;
    private readonly Action<string> _log;

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

    public ThumbnailService(Action<string> log)
    {
        _log = log;
        _diskCachePath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
            "OmniSpot",
            "thumbcache"
        );
        
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
            // Path validation
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                Interlocked.Increment(ref _failures);
                return null;
            }

            var fileInfo = new FileInfo(path);
            var key = new ThumbnailKey(path, size, fileInfo.LastWriteTimeUtc.Ticks);

            // 1. Memory cache check (O(1))
            lock (_memoryCache)
            {
                if (_memoryCache.TryGetValue(key, out var cachedImage))
                {
                    Interlocked.Increment(ref _memoryHits);
                    return cachedImage;
                }
            }

            // 2. Disk cache check
            var diskCachePath = Path.Combine(_diskCachePath, key.GetCacheFileName());
            if (File.Exists(diskCachePath))
            {
                try
                {
                    var diskImage = LoadFromDiskCache(diskCachePath);
                    if (diskImage != null)
                    {
                        Interlocked.Increment(ref _diskHits);
                        AddToMemoryCache(key, diskImage);
                        return diskImage;
                    }
                }
                catch
                {
                    // Ignore disk cache errors
                }
            }

            // 3. Generate thumbnail (with concurrency control)
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
                // Double-check memory cache (race condition protection)
                lock (_memoryCache)
                {
                    if (_memoryCache.TryGetValue(key, out var cachedImage))
                    {
                        return cachedImage;
                    }
                }

                return await Task.Run(() =>
                {
                    try
                    {
                        var thumbnail = GenerateShellThumbnail(path, size);
                        if (thumbnail != null)
                        {
                            // Freeze for cross-thread access
                            thumbnail.Freeze();

                            Interlocked.Increment(ref _shellGenerated);
                            AddToMemoryCache(key, thumbnail);
                            SaveToDiskCache(diskCachePath, thumbnail);
                        }
                        else
                        {
                            Interlocked.Increment(ref _failures);
                        }
                        return thumbnail;
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
            // DOĞRU YÖNTEM: ShellFile direkt BitmapSource verir (alpha kanal korunur)
            using var shellFile = ShellFile.FromFilePath(path);
            
            var bitmapSource = shellFile.Thumbnail.BitmapSource;
            
            if (bitmapSource == null)
            {
                return null;
            }

            // Freeze for thread safety
            if (!bitmapSource.IsFrozen)
                bitmapSource.Freeze();

            return bitmapSource;
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

        lock (_memoryCache)
        {
            // Simple eviction: remove first item if cache is full
            if (_memoryCache.Count >= _maxMemoryCacheCount)
            {
                var firstKey = _memoryCache.Keys.First();
                _memoryCache.Remove(firstKey);
                Interlocked.Increment(ref _evictions);
                _log($"🗑️ Memory cache evicted: {Path.GetFileName(firstKey.Path)}");
            }
            
            _memoryCache[key] = image;
        }
    }

    private BitmapImage? LoadFromDiskCache(string cachePath)
    {
        try
        {
            var bitmap = new BitmapImage();
            bitmap.BeginInit();
            bitmap.CacheOption = BitmapCacheOption.OnLoad;
            bitmap.UriSource = new Uri(cachePath, UriKind.Absolute);
            bitmap.EndInit();
            bitmap.Freeze();
            return bitmap;
        }
        catch
        {
            return null;
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
            // Ignore disk cache save errors
        }
    }

    public void ClearMemoryCache()
    {
        lock (_memoryCache)
        {
            var count = _memoryCache.Count;
            _memoryCache.Clear();
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
            // Ignore clear errors
        }
    }

    public (int memoryCount, int maxMemory) GetCacheStats()
    {
        lock (_memoryCache)
        {
            return (_memoryCache.Count, _maxMemoryCacheCount);
        }
    }

    public ThumbnailDiagnostics GetDiagnostics()
    {
        int count;
        long decodedBytes = 0;
        lock (_memoryCache)
        {
            count = _memoryCache.Count;
            foreach (var image in _memoryCache.Values)
            {
                if (image is BitmapSource bitmap)
                {
                    decodedBytes += (long)bitmap.PixelWidth
                        * bitmap.PixelHeight
                        * bitmap.Format.BitsPerPixel
                        / 8;
                }
            }
        }

        var diskCache = Volatile.Read(ref _diskCacheStats);

        return new ThumbnailDiagnostics(
            count,
            _maxMemoryCacheCount,
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
