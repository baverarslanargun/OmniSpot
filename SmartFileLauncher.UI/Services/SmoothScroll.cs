using System.Windows;
using System.Windows.Controls;
using System.Windows.Media.Animation;
using Point = System.Windows.Point;

namespace SmartFileLauncher.UI.Services;

internal static class SmoothScroll
{
    private const double Breathing = 8;

    private static readonly DependencyProperty OffsetProperty = DependencyProperty.RegisterAttached(
        "Offset", typeof(double), typeof(SmoothScroll),
        new PropertyMetadata(0d, (element, e) => ((ScrollViewer)element).ScrollToVerticalOffset((double)e.NewValue)));

    private static readonly DependencyProperty TargetProperty = DependencyProperty.RegisterAttached(
        "Target", typeof(double), typeof(SmoothScroll), new PropertyMetadata(double.NaN));

    internal static bool BringIntoView(ScrollViewer viewer, FrameworkElement item, bool animate)
    {
        if (!item.IsVisible || !viewer.IsAncestorOf(item) || viewer.ViewportHeight <= 0) return false;
        var top = item.TransformToAncestor(viewer).Transform(new Point(0, 0)).Y;
        if (TargetOffset(top, item.ActualHeight, viewer.ViewportHeight, viewer.VerticalOffset, Pending(viewer)) is { } target)
            To(viewer, target, animate);
        return true;
    }

    internal static double? TargetOffset(double top, double height, double viewport, double offset, double pending)
    {
        top -= pending - offset;
        var bottom = top + height;
        var delta = top < 0 ? top - Breathing
            : bottom > viewport ? Math.Min(bottom - viewport + Breathing, top)
            : 0;
        return delta == 0 ? null : pending + delta;
    }

    internal static void To(ScrollViewer viewer, double offset, bool animate)
    {
        offset = Math.Clamp(offset, 0, viewer.ScrollableHeight);
        viewer.SetValue(TargetProperty, offset);
        if (!animate)
        {
            viewer.BeginAnimation(OffsetProperty, null);
            viewer.ScrollToVerticalOffset(offset);
            viewer.SetValue(TargetProperty, double.NaN);
            return;
        }
        var animation = new DoubleAnimation(viewer.VerticalOffset, offset, MotionTiming.Duration)
        {
            EasingFunction = MotionTiming.Easing()
        };
        animation.Completed += (_, _) =>
        {
            if ((double)viewer.GetValue(TargetProperty) == offset) viewer.SetValue(TargetProperty, double.NaN);
        };
        viewer.BeginAnimation(OffsetProperty, animation);
    }

    private static double Pending(ScrollViewer viewer)
    {
        var target = (double)viewer.GetValue(TargetProperty);
        return double.IsNaN(target) ? viewer.VerticalOffset : target;
    }
}
