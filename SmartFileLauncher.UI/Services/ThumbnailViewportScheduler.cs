using System.Windows.Media;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Services;

internal readonly record struct ThumbnailViewport(int FirstVisibleIndex, int VisibleCount);

internal sealed class ThumbnailViewportScheduler
{
    private readonly IThumbnailService _thumbnails;
    private readonly Func<DesktopIconViewModel, ImageSource, Task> _applyAsync;
    private readonly Func<int, CancellationToken, Task>? _delayAsync;
    private readonly int _thumbnailSize;
    private readonly int _batchSize;
    private readonly int _batchDelayMilliseconds;
    private readonly int _prefetchScreens;

    private readonly HashSet<DesktopIconViewModel> _requested = new();

    private IReadOnlyList<DesktopIconViewModel> _items = Array.Empty<DesktopIconViewModel>();
    private ThumbnailViewport _lastViewport;
    private List<DesktopIconViewModel>? _currentBatch;
    private CancellationTokenSource? _current;
    private Task _currentTask = Task.CompletedTask;
    private long _scheduled;
    private long _released;

    public ThumbnailViewportScheduler(
        IThumbnailService thumbnails,
        Func<DesktopIconViewModel, ImageSource, Task> applyAsync,
        int thumbnailSize,
        int batchSize,
        int prefetchScreens = 1,
        int batchDelayMilliseconds = 10,
        Func<int, CancellationToken, Task>? delayAsync = null)
    {
        if (prefetchScreens < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(prefetchScreens));
        }

        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        _thumbnailSize = thumbnailSize;
        _batchSize = batchSize;
        _prefetchScreens = prefetchScreens;
        _batchDelayMilliseconds = batchDelayMilliseconds;
        _delayAsync = delayAsync;
    }

    internal Task Current => _currentTask;

    public long ScheduledCount => Interlocked.Read(ref _scheduled);

    public long ReleasedCount => Interlocked.Read(ref _released);

    public void Reset(IReadOnlyList<DesktopIconViewModel> items)
    {
        Cancel();
        _requested.Clear();
        _lastViewport = default;
        _items = items ?? Array.Empty<DesktopIconViewModel>();
    }

    public void Cancel()
    {
        var previous = _current;
        _current = null;
        previous?.Cancel();

        var batch = _currentBatch;
        _currentBatch = null;
        if (batch == null)
        {
            return;
        }

        foreach (var item in batch)
        {
            if (item.Thumbnail == null)
            {
                _requested.Remove(item);
            }
        }
    }

    public void Update(ThumbnailViewport viewport)
    {
        var items = _items;
        if (items.Count == 0)
        {
            Cancel();
            return;
        }

        if (viewport.VisibleCount <= 0)
        {
            Cancel();
            return;
        }

        _lastViewport = viewport;

        Cancel();

        var load = Window(items.Count, viewport, _prefetchScreens);
        var missing = new List<DesktopIconViewModel>();
        for (var index = load.Start; index < load.End; index++)
        {
            var item = items[index];
            if (item.Thumbnail != null || !_requested.Add(item))
            {
                continue;
            }

            missing.Add(item);
        }

        if (missing.Count == 0)
        {
            return;
        }

        Interlocked.Add(ref _scheduled, missing.Count);
        var cancellation = new CancellationTokenSource();
        _current = cancellation;
        _currentBatch = missing;
        _currentTask = RunAsync(missing, cancellation);
    }

    public void ReleaseOutsideViewport()
    {
        var items = _items;
        if (items.Count == 0)
        {
            return;
        }

        var keep = Window(items.Count, _lastViewport, _prefetchScreens);
        for (var index = 0; index < items.Count; index++)
        {
            if (index >= keep.Start && index < keep.End)
            {
                continue;
            }

            if (items[index].Thumbnail == null)
            {
                continue;
            }

            items[index].Thumbnail = null;
            _requested.Remove(items[index]);
            Interlocked.Increment(ref _released);
        }
    }

    internal static (int Start, int End) Window(
        int itemCount,
        ThumbnailViewport viewport,
        int screens)
    {
        if (itemCount <= 0 || viewport.VisibleCount <= 0)
        {
            return (0, 0);
        }

        var margin = viewport.VisibleCount * screens;
        var start = Math.Max(0, viewport.FirstVisibleIndex - margin);
        var end = Math.Min(
            itemCount,
            viewport.FirstVisibleIndex + viewport.VisibleCount + margin);
        return start >= end ? (0, 0) : (start, end);
    }

    private async Task RunAsync(
        List<DesktopIconViewModel> items,
        CancellationTokenSource cancellation)
    {
        try
        {
            var loader = new ThumbnailBatchLoader(
                _thumbnails,
                _applyAsync,
                _thumbnailSize,
                _batchSize,
                _batchDelayMilliseconds,
                _delayAsync);
            await loader.RunAsync(items, cancellation.Token);
        }
        catch (OperationCanceledException)
        {
        }
        finally
        {
            if (ReferenceEquals(_current, cancellation))
            {
                _current = null;
            }

            if (ReferenceEquals(_currentBatch, items))
            {
                _currentBatch = null;
            }

            cancellation.Dispose();
        }
    }
}
