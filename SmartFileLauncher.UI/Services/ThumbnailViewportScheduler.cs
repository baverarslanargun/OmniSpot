using System.Windows.Media;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Services;

/// <summary>
/// Görünür öğe aralığı. <see cref="VisibleCount"/> sıfırsa görünüm ölçülemiyor
/// veya gizli demektir.
/// </summary>
internal readonly record struct ThumbnailViewport(int FirstVisibleIndex, int VisibleCount);

/// <summary>
/// Küçük resimleri yalnız görünür alan ve sınırlı prefetch için yükler.
/// Bir kez yüklenen görsel kaydırma sırasında bırakılmaz; yalnız uygulama
/// bir süre kullanılmadığında <see cref="ReleaseOutsideViewport"/> ile
/// görünür pencere dışı boşaltılır. Terk edilen görünümün işi iptal edilir
/// ve eski sonuç UI'ya uygulanmaz.
/// </summary>
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

    /// <summary>Yürüyen yükleme turu; testler bunu bekler.</summary>
    internal Task Current => _currentTask;

    public long ScheduledCount => Interlocked.Read(ref _scheduled);

    public long ReleasedCount => Interlocked.Read(ref _released);

    /// <summary>Görünüm değişti: önceki turu iptal eder ve yeni listeyi hedefler.</summary>
    public void Reset(IReadOnlyList<DesktopIconViewModel> items)
    {
        Cancel();
        _requested.Clear();
        _lastViewport = default;
        _items = items ?? Array.Empty<DesktopIconViewModel>();
    }

    /// <summary>Yürüyen turu iptal eder; yeni istek açılmaz.</summary>
    public void Cancel()
    {
        var previous = _current;
        _current = null;
        previous?.Cancel();

        // İptal edilen turda sırası gelmemiş öğeler istenmiş sayılmaz; hâlâ
        // görünür alandaysalar bir sonraki turda yeniden istenebilmeliler.
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

    /// <summary>
    /// Görünür aralık değişti. Tutma penceresi dışındaki görseller serbest
    /// bırakılır, yükleme penceresindeki eksikler istenir.
    /// </summary>
    public void Update(ThumbnailViewport viewport)
    {
        var items = _items;
        if (items.Count == 0)
        {
            Cancel();
            return;
        }

        // Görünüm ölçülemiyorsa hiçbir şey yükleme, ama serbest de bırakma:
        // ölçülemeyen bir anı "hiçbir şey görünmüyor" saymak titremeye yol açar.
        if (viewport.VisibleCount <= 0)
        {
            Cancel();
            return;
        }

        _lastViewport = viewport;

        // Önce iptal: yarım kalan istekler yeniden değerlendirmeye girsin.
        Cancel();

        var load = Window(items.Count, viewport, _prefetchScreens);
        var missing = new List<DesktopIconViewModel>();
        for (var index = load.Start; index < load.End; index++)
        {
            var item = items[index];
            // Zaten yüklü olan istenmez; küçük resmi üretilemeyen öğe de bu
            // görünüm boyunca tekrar istenmez, aksi hâlde her kaydırmada
            // sonuçsuz kabuk çağrısı açılır.
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

    /// <summary>
    /// Uygulama bir süredir kullanılmıyor: son görünür pencerenin dışında kalan
    /// küçük resimleri bellekten düşürür. Kaydırma sırasında çağrılmaz — bir kez
    /// yüklenen görsel kullanım boyunca yerinde kalır.
    /// </summary>
    public void ReleaseOutsideViewport()
    {
        var items = _items;
        if (items.Count == 0)
        {
            return;
        }

        // Son görünür pencere korunur ki uygulamaya dönüldüğünde ekran hazır olsun.
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
            // Bırakılan öğe geri kaydırıldığında yeniden istenebilmeli.
            _requested.Remove(items[index]);
            Interlocked.Increment(ref _released);
        }
    }

    /// <summary>
    /// Görünür aralığın <paramref name="screens"/> ekran öncesi ve sonrasını
    /// kapsayan, listeye kırpılmış pencere.
    /// </summary>
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

            // Normal biten turda küçük resmi üretilemeyen öğe istenmiş kalır;
            // aynı görünümde tekrar denenmez.
            if (ReferenceEquals(_currentBatch, items))
            {
                _currentBatch = null;
            }

            cancellation.Dispose();
        }
    }
}
