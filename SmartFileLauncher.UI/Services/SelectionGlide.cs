using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Color = System.Windows.Media.Color;

namespace SmartFileLauncher.UI.Services;

internal sealed class SelectionGlide
{
    internal static readonly Color Accent = Color.FromRgb(0x00, 0x7A, 0xFF);
    internal static readonly Color Wash = Color.FromArgb(0x11, 0x00, 0x7A, 0xFF);
    private static readonly SolidColorBrush WashBrush = Frozen(Wash);
    private readonly Canvas _layer;
    private readonly Border _ring;
    private FrameworkElement? _target;
    private Rect _from;
    private long _started;
    private Action? _arrived;

    internal SelectionGlide(Canvas layer)
    {
        _layer = layer;
        _ring = new Border
        {
            BorderBrush = Frozen(Accent),
            Background = WashBrush,
            BorderThickness = new Thickness(1),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        layer.Children.Add(_ring);
    }

    internal bool Running => _target != null;

    internal bool Start(Border from, Border to, Action arrived, bool filled = true)
    {
        Rect? current = Running && _ring.Visibility == Visibility.Visible ? CurrentRing() : null;
        Finish();
        if (!_layer.IsVisible || !to.IsVisible || !SameWindow(to)) return false;
        var start = current ?? (from.IsVisible && SameWindow(from) ? Bounds(from) : null);
        if (start is null) return false;
        _from = start.Value;
        _target = to;
        _arrived = arrived;
        _started = Stopwatch.GetTimestamp();
        _ring.CornerRadius = to.CornerRadius;
        _ring.Background = filled ? WashBrush : System.Windows.Media.Brushes.Transparent;
        Place(_from);
        _ring.Visibility = Visibility.Visible;
        CompositionTarget.Rendering += OnFrame;
        return true;
    }

    internal void Finish()
    {
        if (_target == null) return;
        CompositionTarget.Rendering -= OnFrame;
        _ring.Visibility = Visibility.Collapsed;
        _target = null;
        var arrived = _arrived;
        _arrived = null;
        arrived?.Invoke();
    }

    internal static Rect Interpolate(Rect from, Rect to, double progress) => new(
        from.X + (to.X - from.X) * progress,
        from.Y + (to.Y - from.Y) * progress,
        Math.Max(0, from.Width + (to.Width - from.Width) * progress),
        Math.Max(0, from.Height + (to.Height - from.Height) * progress));

    private void OnFrame(object? sender, EventArgs e)
    {
        if (_target is not { IsVisible: true } target || Bounds(target) is not { } to)
        {
            Finish();
            return;
        }
        var t = Stopwatch.GetElapsedTime(_started) / MotionTiming.Length;
        Place(Interpolate(_from, to, MotionTiming.Ease(t)));
        if (t >= 1) Finish();
    }

    private Rect CurrentRing() => new(Canvas.GetLeft(_ring), Canvas.GetTop(_ring), _ring.Width, _ring.Height);

    private void Place(Rect rect)
    {
        Canvas.SetLeft(_ring, rect.X);
        Canvas.SetTop(_ring, rect.Y);
        _ring.Width = rect.Width;
        _ring.Height = rect.Height;
    }

    private bool SameWindow(Visual element) =>
        PresentationSource.FromVisual(element) is { } source && ReferenceEquals(source, PresentationSource.FromVisual(_layer));

    private Rect? Bounds(FrameworkElement element)
    {
        try
        {
            return element.TransformToVisual(_layer).TransformBounds(new Rect(element.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return null;
        }
    }

    private static SolidColorBrush Frozen(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }
}
