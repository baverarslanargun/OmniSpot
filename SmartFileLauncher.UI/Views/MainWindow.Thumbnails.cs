using System.Windows;
using System.Windows.Input;
using System.Windows.Media;
using SmartFileLauncher.UI.Services;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private readonly ThumbnailActivityTracker _thumbnailActivity = new(
        () => TimeSpan.FromMilliseconds(Environment.TickCount64));
    private CancellationTokenSource? _searchThumbnailCancellation;
    private IThumbnailCacheControl? _thumbnailCacheControl;
    private System.Drawing.Point? _lastThumbnailMousePosition;
    private int _thumbnailBudgetTicks;
    private int _thumbnailTrimPending;

    private void InitializeThumbnailPolicy()
    {
        _thumbnailCacheControl = _thumbnailService as IThumbnailCacheControl;
        if (_thumbnailCacheControl != null) _thumbnailCacheControl.CacheTrimmed += HandleThumbnailCacheTrimmed;
        InputManager.Current.PreProcessInput += HandleThumbnailInput;
        Activated += HandleThumbnailActivation;
        _thumbnailIdleRelease.Interval = TimeSpan.FromSeconds(1);
        _thumbnailIdleRelease.Tick += HandleThumbnailPolicyTick;
        ApplyThumbnailSettings();
        _thumbnailIdleRelease.Start();
    }

    private void ApplyThumbnailSettings()
    {
        _thumbnailViewport?.Cancel();
        CancelSearchThumbnailRequests();
        _thumbnailCacheControl?.Configure(ThumbnailMemoryPolicy.Configure(_appSettings, SystemMemoryReader.Read()));
        ReleaseUnretainedThumbnails();
        _thumbnailViewport?.Reset(_desktopIcons.ToArray());
        if (_appSettings.ThumbnailPreviewsEnabled && !_thumbnailActivity.IsIdle)
        {
            _thumbnailCacheControl?.Resume();
            ScheduleViewportUpdate();
            ReloadSearchThumbnails();
        }
    }

    private void HandleThumbnailPolicyTick(object? sender, EventArgs e)
    {
        if (_isPreparedForShutdown) return;
        if (++_thumbnailBudgetTicks >= 5)
        {
            _thumbnailBudgetTicks = 0;
            _thumbnailCacheControl?.Configure(ThumbnailMemoryPolicy.Configure(_appSettings, SystemMemoryReader.Read()));
        }
        if (!_thumbnailActivity.Check(TimeSpan.FromSeconds(ThumbnailMemoryPolicy.IdleSeconds(_appSettings)))) return;
        _viewportDebounce.Stop();
        _thumbnailViewport?.Cancel();
        CancelSearchThumbnailRequests();
        _thumbnailCacheControl?.EnterIdle();
        ReleaseUnretainedThumbnails();
    }

    private void HandleThumbnailInput(object sender, PreProcessInputEventArgs e)
    {
        var input = e.StagingItem.Input;
        if (input.RoutedEvent == Mouse.PreviewMouseMoveEvent)
        {
            var position = System.Windows.Forms.Control.MousePosition;
            if (_lastThumbnailMousePosition == position) return;
            _lastThumbnailMousePosition = position;
        }
        else if (input.RoutedEvent != Mouse.PreviewMouseDownEvent
            && input.RoutedEvent != Mouse.PreviewMouseWheelEvent
            && input.RoutedEvent != Keyboard.PreviewKeyDownEvent
            && input.RoutedEvent != UIElement.PreviewTouchDownEvent
            && input.RoutedEvent != UIElement.PreviewTouchMoveEvent)
            return;
        RecordThumbnailActivity();
    }

    private void HandleThumbnailActivation(object? sender, EventArgs e) => RecordThumbnailActivity();

    private void RecordThumbnailActivity()
    {
        if (_isPreparedForShutdown || !_thumbnailActivity.RecordActivity()) return;
        _thumbnailCacheControl?.Resume();
        if (!_appSettings.ThumbnailPreviewsEnabled) return;
        CancelSearchThumbnailRequests();
        _thumbnailViewport?.Reset(_desktopIcons.ToArray());
        ScheduleViewportUpdate();
        ReloadSearchThumbnails();
    }

    private bool CanApplyThumbnail(ImageSource thumbnail) => !_isPreparedForShutdown
        && _appSettings.ThumbnailPreviewsEnabled && !_thumbnailActivity.IsIdle
        && (_thumbnailCacheControl?.IsRetained(thumbnail) ?? true);

    private void ReloadSearchThumbnails()
    {
        foreach (var item in _searchResults)
            if (item.Thumbnail == null) _ = LoadSearchThumbnailAsync(item);
    }

    private void CancelSearchThumbnailRequests(bool stopping = false)
    {
        var previous = _searchThumbnailCancellation;
        _searchThumbnailCancellation = stopping ? null
            : CancellationTokenSource.CreateLinkedTokenSource(_lifetimeCancellation.Token);
        previous?.Cancel();
        previous?.Dispose();
    }

    private void HandleThumbnailCacheTrimmed()
    {
        if (_isPreparedForShutdown || Interlocked.Exchange(ref _thumbnailTrimPending, 1) != 0) return;
        _ = Dispatcher.InvokeAsync(() =>
        {
            Interlocked.Exchange(ref _thumbnailTrimPending, 0);
            if (!_isPreparedForShutdown) ReleaseUnretainedThumbnails();
        });
    }

    private void ReleaseUnretainedThumbnails()
    {
        bool Retained(ImageSource image) => _appSettings.ThumbnailPreviewsEnabled
            && (_thumbnailCacheControl?.IsRetained(image) ?? false);
        _thumbnailViewport?.ReleaseUnretained(Retained);
        foreach (var icon in _desktopIcons)
            if (icon.Thumbnail is { } image && !Retained(image)) icon.Thumbnail = null;
        foreach (var result in _searchResults)
            if (result.Thumbnail is { } image && !Retained(image)) result.Thumbnail = null;
    }

    private void ShutdownThumbnailPolicy()
    {
        InputManager.Current.PreProcessInput -= HandleThumbnailInput;
        Activated -= HandleThumbnailActivation;
        _thumbnailIdleRelease.Tick -= HandleThumbnailPolicyTick;
        if (_thumbnailCacheControl != null) _thumbnailCacheControl.CacheTrimmed -= HandleThumbnailCacheTrimmed;
        CancelSearchThumbnailRequests(stopping: true);
        _thumbnailCacheControl?.Configure(new(false, 0, 0, Array.Empty<string>()));
        ReleaseUnretainedThumbnails();
    }
}
