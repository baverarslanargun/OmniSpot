using System.Windows.Media;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Services;

/// <summary>
/// Klasör ikonlarının küçük resimlerini batch'ler hâlinde yükler. İptal edildiğinde
/// kalan batch'ler için yeni istek açılmaz; terk edilen klasörün işi yeni klasörün
/// üstüne binmez.
/// </summary>
internal sealed class ThumbnailBatchLoader
{
    private readonly IThumbnailService _thumbnails;
    private readonly Func<DesktopIconViewModel, ImageSource, Task> _applyAsync;
    private readonly Func<int, CancellationToken, Task> _delayAsync;
    private readonly int _thumbnailSize;
    private readonly int _batchSize;
    private readonly int _batchDelayMilliseconds;

    public ThumbnailBatchLoader(
        IThumbnailService thumbnails,
        Func<DesktopIconViewModel, ImageSource, Task> applyAsync,
        int thumbnailSize,
        int batchSize,
        int batchDelayMilliseconds = 10,
        Func<int, CancellationToken, Task>? delayAsync = null)
    {
        if (thumbnailSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(thumbnailSize));
        }

        if (batchSize <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(batchSize));
        }

        _thumbnails = thumbnails ?? throw new ArgumentNullException(nameof(thumbnails));
        _applyAsync = applyAsync ?? throw new ArgumentNullException(nameof(applyAsync));
        _thumbnailSize = thumbnailSize;
        _batchSize = batchSize;
        _batchDelayMilliseconds = batchDelayMilliseconds;
        _delayAsync = delayAsync ?? ((milliseconds, token) => Task.Delay(milliseconds, token));
    }

    public async Task RunAsync(
        IReadOnlyList<DesktopIconViewModel> items,
        CancellationToken token)
    {
        for (var start = 0; start < items.Count; start += _batchSize)
        {
            if (token.IsCancellationRequested)
            {
                return;
            }

            var end = Math.Min(start + _batchSize, items.Count);
            var batch = new Task[end - start];
            for (var index = start; index < end; index++)
            {
                batch[index - start] = LoadAsync(items[index], token);
            }

            await Task.WhenAll(batch).ConfigureAwait(true);

            if (token.IsCancellationRequested || _batchDelayMilliseconds <= 0)
            {
                continue;
            }

            try
            {
                await _delayAsync(_batchDelayMilliseconds, token).ConfigureAwait(true);
            }
            catch (OperationCanceledException)
            {
                return;
            }
        }
    }

    private async Task LoadAsync(DesktopIconViewModel item, CancellationToken token)
    {
        if (token.IsCancellationRequested)
        {
            return;
        }

        try
        {
            var thumbnail = await _thumbnails
                .GetThumbnailAsync(item.FullPath, _thumbnailSize, token)
                .ConfigureAwait(true);

            if (thumbnail != null && !token.IsCancellationRequested)
            {
                await _applyAsync(item, thumbnail).ConfigureAwait(true);
            }
        }
        catch
        {
        }
    }
}
