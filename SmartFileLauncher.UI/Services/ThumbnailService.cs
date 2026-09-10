using System.IO;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using Microsoft.WindowsAPICodePack.Shell;

namespace SmartFileLauncher.UI.Services;

public class ThumbnailService : IThumbnailService, IThumbnailCacheControl
{
    internal const int DefaultMaxMemoryCacheCount = 1000;
    internal const long DefaultMaxMemoryCacheBytes = 64L * 1024 * 1024;

    private sealed record CacheEntry(ThumbnailKey Key, ImageSource Image, long Bytes);

    private readonly Dictionary<ThumbnailKey, LinkedListNode<CacheEntry>> _memoryCache = new();
    private readonly LinkedList<CacheEntry> _recency = new();
    private readonly object _memoryCacheLock = new();
    private readonly SemaphoreSlim _semaphore = new(4);
    private int _maxMemoryCacheCount;
    private long _maxMemoryCacheBytes;
    private bool _enabled = true;
    private bool _idle;
    private long _epoch;
    private string[] _pinnedFolders = Array.Empty<string>();
    private readonly Dictionary<ImageSource, int> _retainedImages = new(ReferenceEqualityComparer.Instance);
    private readonly Func<string, int, BitmapSource?> _generateThumbnail;
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

    public event Action? CacheTrimmed;

    internal ThumbnailService(Action<string> log, string diskCachePath, Func<string, int, BitmapSource?> generator)
        : this(log, diskCachePath)
    {
        _generateThumbnail = generator;
    }

    public ThumbnailService(
        Action<string> log,
        string? diskCachePath = null,
        int maxMemoryCacheCount = DefaultMaxMemoryCacheCount,
        long maxMemoryCacheBytes = DefaultMaxMemoryCacheBytes)
    {
        _log = log;
        _generateThumbnail = GenerateShellThumbnail;
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
        long epoch;
        lock (_memoryCacheLock)
        {
            if (!_enabled || _idle || _maxMemoryCacheCount == 0 || _maxMemoryCacheBytes == 0 || token.IsCancellationRequested)
                return null;
            epoch = _epoch;
        }
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

            if (TryGetFromMemoryCache(key, epoch, token, out var cachedImage))
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
                        return AddToMemoryCache(key, diskImage, epoch, token) ? diskImage : null;
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
                if (TryGetFromMemoryCache(key, epoch, token, out cachedImage))
                {
                    return cachedImage;
                }

                return await Task.Run(() =>
                {
                    try
                    {
                        if (!IsCurrent(epoch, token)) return null;
                        var thumbnail = _generateThumbnail(path, size);
                        if (thumbnail == null)
                        {
                            Interlocked.Increment(ref _failures);
                            return null;
                        }

                        if (!IsCurrent(epoch, token)) return null;
                        if (!thumbnail.IsFrozen)
                        {
                            thumbnail.Freeze();
                        }

                        var bounded = StoreAndBound(diskCachePath, thumbnail, size);
                        Interlocked.Increment(ref _shellGenerated);
                        return AddToMemoryCache(key, bounded, epoch, token) ? bounded : null;
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
            shellThumbnail.FormatOption = ShellThumbnailFormatOption.ThumbnailOnly;
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

    private bool TryGetFromMemoryCache(ThumbnailKey key, long epoch, CancellationToken token, out ImageSource? image)
    {
        lock (_memoryCacheLock)
        {
            if (IsCurrent(epoch, token) && _memoryCache.TryGetValue(key, out var node))
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

    private bool AddToMemoryCache(ThumbnailKey key, ImageSource image, long epoch, CancellationToken token)
    {
        if (image is BitmapSource bitmap)
        {
            Volatile.Write(ref _lastDecodedPixelWidth, bitmap.PixelWidth);
            Volatile.Write(ref _lastDecodedPixelHeight, bitmap.PixelHeight);
        }

        var bytes = GetDecodedByteCount(image);

        var trimmed = false;
        lock (_memoryCacheLock)
        {
            if (!IsCurrent(epoch, token) || _maxMemoryCacheCount == 0 || bytes > _maxMemoryCacheBytes)
                return false;
            if (_memoryCache.TryGetValue(key, out var existing))
            {
                RemoveEntry(existing);
                trimmed = true;
            }

            var node = _recency.AddFirst(new CacheEntry(key, image, bytes));
            _memoryCache[key] = node;
            _memoryCacheBytes += bytes;
            _retainedImages[image] = _retainedImages.GetValueOrDefault(image) + 1;
            trimmed |= TrimToBudget();
        }
        if (trimmed) CacheTrimmed?.Invoke();
        return true;
    }

    private bool IsCurrent(long epoch, CancellationToken token)
    {
        lock (_memoryCacheLock)
            return epoch == _epoch && _enabled && !_idle && !token.IsCancellationRequested;
    }

    public bool IsRetained(ImageSource image)
    {
        lock (_memoryCacheLock) return _enabled && _retainedImages.ContainsKey(image);
    }

    public void Configure(ThumbnailCacheConfiguration configuration)
    {
        lock (_memoryCacheLock)
        {
            var count = Math.Max(0, configuration.MaxCount);
            var bytes = Math.Clamp(configuration.MaxBytes, 0, ThumbnailMemoryPolicy.AbsoluteMaxBytes);
            if (_enabled == configuration.Enabled && _maxMemoryCacheCount == count
                && _maxMemoryCacheBytes == bytes && _pinnedFolders.SequenceEqual(configuration.PinnedFolders))
                return;
            _epoch++;
            _enabled = configuration.Enabled;
            _maxMemoryCacheCount = count;
            _maxMemoryCacheBytes = bytes;
            _pinnedFolders = configuration.PinnedFolders.ToArray();
            RemoveUnpinnedEntries(all: !_enabled);
            TrimToBudget();
        }
        CacheTrimmed?.Invoke();
    }

    public void EnterIdle()
    {
        lock (_memoryCacheLock)
        {
            _idle = true;
            _epoch++;
            RemoveUnpinnedEntries(all: false);
        }
        CacheTrimmed?.Invoke();
    }

    public void Resume()
    {
        lock (_memoryCacheLock) _idle = false;
    }

    private void RemoveUnpinnedEntries(bool all)
    {
        for (var node = _recency.Last; node != null;)
        {
            var previous = node.Previous;
            if (all || (_idle && !ThumbnailMemoryPolicy.IsPinned(node.Value.Key.Path, _pinnedFolders)))
                RemoveEntry(node);
            node = previous;
        }
    }

    private bool TrimToBudget()
    {
        var trimmed = false;
        while (_recency.Last is { } last && (_memoryCache.Count > _maxMemoryCacheCount || _memoryCacheBytes > _maxMemoryCacheBytes))
        {
            RemoveEntry(last);
            trimmed = true;
        }
        return trimmed;
    }

    private void RemoveEntry(LinkedListNode<CacheEntry> node)
    {
        _recency.Remove(node);
        _memoryCache.Remove(node.Value.Key);
        _memoryCacheBytes -= node.Value.Bytes;
        var references = _retainedImages[node.Value.Image] - 1;
        if (references == 0) _retainedImages.Remove(node.Value.Image);
        else _retainedImages[node.Value.Image] = references;
        Interlocked.Increment(ref _evictions);
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
            _epoch++;
            _memoryCache.Clear();
            _recency.Clear();
            _retainedImages.Clear();
            _memoryCacheBytes = 0;
            _log($"🗑️ Memory cache cleared: {count} items");
        }
        CacheTrimmed?.Invoke();
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
