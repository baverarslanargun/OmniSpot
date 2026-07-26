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
        try
        {
            // Path validation
            if (!File.Exists(path) && !Directory.Exists(path))
            {
                return null;
            }

            var fileInfo = new FileInfo(path);
            var key = new ThumbnailKey(path, size, fileInfo.LastWriteTimeUtc.Ticks);

            // 1. Memory cache check (O(1))
            lock (_memoryCache)
            {
                if (_memoryCache.TryGetValue(key, out var cachedImage))
                {
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
            await _semaphore.WaitAsync(token);
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
                            
                            AddToMemoryCache(key, thumbnail);
                            SaveToDiskCache(diskCachePath, thumbnail);
                        }
                        return thumbnail;
                    }
                    catch
                    {
                        return null;
                    }
                }, token);
            }
            finally
            {
                _semaphore.Release();
            }
        }
        catch
        {
            return null;
        }
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
        lock (_memoryCache)
        {
            // Simple eviction: remove first item if cache is full
            if (_memoryCache.Count >= _maxMemoryCacheCount)
            {
                var firstKey = _memoryCache.Keys.First();
                _memoryCache.Remove(firstKey);
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
}
