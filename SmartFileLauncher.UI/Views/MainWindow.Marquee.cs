using System.Collections;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using SmartFileLauncher.UI.Services;
using Color = System.Windows.Media.Color;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;

namespace SmartFileLauncher.UI.Views;

public partial class MainWindow
{
    private const double MarqueeEdge = 24;
    private const double MarqueeMaximumStep = 28;
    private readonly Dictionary<object, Rect> _marqueeBounds = new(ReferenceEqualityComparer.Instance);
    private HashSet<object> _marqueeBase = new(ReferenceEqualityComparer.Instance);
    private Border? _marqueeBox;
    private ScrollViewer? _marqueeViewer;
    private bool _marqueeSearch;
    private bool _marqueePending;
    private bool _marqueeActive;
    private Point _marqueeStart;
    private Point _marqueePointer;
    private Point _marqueeDown;

    private void InitializeMarquee()
    {
        PreviewMouseMove += MarqueeMouseMove;
        PreviewMouseLeftButtonUp += MarqueeMouseUp;
        LostMouseCapture += (_, _) => EndMarquee();
    }

    private Border MarqueeBox => _marqueeBox ??= CreateMarqueeBox();

    private Border CreateMarqueeBox()
    {
        var box = new Border
        {
            BorderBrush = FrozenBrush(Color.FromRgb(0x00, 0x7A, 0xFF)),
            Background = FrozenBrush(Color.FromArgb(0x33, 0x00, 0x7A, 0xFF)),
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(2),
            IsHitTestVisible = false,
            Visibility = Visibility.Collapsed
        };
        SelectionGlideLayer.Children.Add(box);
        return box;
    }

    private static SolidColorBrush FrozenBrush(Color color)
    {
        var brush = new SolidColorBrush(color);
        brush.Freeze();
        return brush;
    }

    private bool ArmMarquee(MouseButtonEventArgs e, bool additive)
    {
        var search = SearchSurfaceVisible;
        return (search ? SearchResultsScroll() : DesktopScroll) is { ActualHeight: > 0 } viewer &&
            ArmMarqueeAt(search, viewer, e.GetPosition(viewer), e.GetPosition(this), additive);
    }

    private bool ArmMarqueeAt(bool search, ScrollViewer viewer, Point viewerPoint, Point windowPoint, bool additive)
    {
        if (!InsideViewport(viewer, viewerPoint)) return false;
        PrepareMarquee(search, viewer, viewerPoint, additive);
        _marqueeDown = windowPoint;
        return true;
    }

    private void PrepareMarquee(bool search, ScrollViewer viewer, Point viewerPoint, bool additive)
    {
        _marqueeSearch = search;
        _marqueeViewer = viewer;
        _marqueeStart = new Point(viewerPoint.X + viewer.HorizontalOffset, viewerPoint.Y + viewer.VerticalOffset);
        _marqueePointer = viewerPoint;
        _marqueeBase = new HashSet<object>(additive ? SelectedFileItems(search) : [], ReferenceEqualityComparer.Instance);
        _marqueeBounds.Clear();
        _marqueePending = true;
    }

    private bool WindowDragAllowed => !_marqueePending && !_marqueeActive;

    private static bool InsideViewport(ScrollViewer viewer, Point point) =>
        point.X >= 0 && point.Y >= 0 && point.X <= viewer.ViewportWidth && point.Y <= viewer.ViewportHeight;

    private void MarqueeMouseMove(object sender, MouseEventArgs e)
    {
        if (!_marqueePending && !_marqueeActive || _marqueeViewer is not { } viewer) return;
        if (e.LeftButton != MouseButtonState.Pressed)
        {
            EndMarquee();
            return;
        }
        if (!_marqueeActive)
        {
            var window = e.GetPosition(this);
            if (Math.Abs(window.X - _marqueeDown.X) < SystemParameters.MinimumHorizontalDragDistance &&
                Math.Abs(window.Y - _marqueeDown.Y) < SystemParameters.MinimumVerticalDragDistance)
                return;
            CaptureMouse();
        }
        DragMarqueeTo(e.GetPosition(viewer));
        e.Handled = true;
    }

    private void DragMarqueeTo(Point viewerPoint)
    {
        if (_marqueeViewer == null) return;
        if (!_marqueeActive)
        {
            _marqueeActive = true;
            _marqueePending = false;
            ResetKeyboardCardEffects();
            CompositionTarget.Rendering += MarqueeFrame;
        }
        _marqueePointer = viewerPoint;
        UpdateMarquee();
    }

    private void MarqueeMouseUp(object sender, MouseButtonEventArgs e)
    {
        if (_marqueeActive) e.Handled = true;
        EndMarquee();
    }

    private void EndMarquee()
    {
        _marqueePending = false;
        if (!_marqueeActive) return;
        _marqueeActive = false;
        CompositionTarget.Rendering -= MarqueeFrame;
        MarqueeBox.Visibility = Visibility.Collapsed;
        if (IsMouseCaptured) ReleaseMouseCapture();
        _marqueeBounds.Clear();
    }

    private void MarqueeFrame(object? sender, EventArgs e)
    {
        if (!_marqueeActive || _marqueeViewer is not { } viewer) return;
        var y = _marqueePointer.Y;
        var step = y < MarqueeEdge ? -Math.Min(MarqueeMaximumStep, (MarqueeEdge - y) / 2)
            : y > viewer.ViewportHeight - MarqueeEdge ? Math.Min(MarqueeMaximumStep, (y - viewer.ViewportHeight + MarqueeEdge) / 2)
            : 0;
        if (step == 0) return;
        var before = viewer.VerticalOffset;
        viewer.ScrollToVerticalOffset(Math.Clamp(before + step, 0, viewer.ScrollableHeight));
        viewer.UpdateLayout();
        if (viewer.VerticalOffset != before) UpdateMarquee();
    }

    private void UpdateMarquee()
    {
        if (_marqueeViewer is not { } viewer) return;
        var current = new Point(
            Math.Clamp(_marqueePointer.X, 0, viewer.ViewportWidth) + viewer.HorizontalOffset,
            Math.Clamp(_marqueePointer.Y + viewer.VerticalOffset, 0, Math.Max(viewer.ExtentHeight, viewer.ViewportHeight)));
        var area = new Rect(_marqueeStart, current);

        var items = MarqueeItems();
        for (var index = 0; index < items.Count; index++)
        {
            if ((_marqueeSearch ? FileSearchFocusTarget(index) : RealizedFolderCard(index)) is not FrameworkElement element ||
                element.ActualWidth <= 0 || !viewer.IsAncestorOf(element))
                continue;
            var bounds = element.TransformToAncestor(viewer).TransformBounds(new Rect(element.RenderSize));
            bounds.Offset(viewer.HorizontalOffset, viewer.VerticalOffset);
            _marqueeBounds[items[index]!] = bounds;
        }

        var chosen = MarqueeChoice(area, _marqueeBounds, _marqueeBase);
        for (var index = 0; index < items.Count; index++)
        {
            var item = items[index]!;
            var wanted = chosen.Contains(item);
            if (IsTransferSelected(item) != wanted) SetTransferSelection(item, wanted);
        }
        UpdateSelectionBar();
        DrawMarquee(viewer, area);
    }

    internal static HashSet<object> MarqueeChoice(Rect area, IReadOnlyDictionary<object, Rect> bounds, IEnumerable<object> keep)
    {
        var chosen = new HashSet<object>(keep, ReferenceEqualityComparer.Instance);
        foreach (var (item, rect) in bounds)
            if (area.IntersectsWith(rect)) chosen.Add(item);
        return chosen;
    }

    private IList MarqueeItems() => _marqueeSearch ? _searchResults : _desktopIcons;

    private void DrawMarquee(ScrollViewer viewer, Rect area)
    {
        var box = MarqueeBox;
        var visible = area;
        visible.Offset(-viewer.HorizontalOffset, -viewer.VerticalOffset);
        visible.Intersect(new Rect(0, 0, viewer.ViewportWidth, viewer.ViewportHeight));
        if (visible.IsEmpty || !SelectionGlideLayer.IsVisible)
        {
            box.Visibility = Visibility.Collapsed;
            return;
        }
        var origin = viewer.TranslatePoint(visible.TopLeft, SelectionGlideLayer);
        Canvas.SetLeft(box, origin.X);
        Canvas.SetTop(box, origin.Y);
        box.Width = visible.Width;
        box.Height = visible.Height;
        box.Visibility = Visibility.Visible;
    }
}
