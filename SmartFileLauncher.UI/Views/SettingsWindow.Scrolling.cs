using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;

namespace SmartFileLauncher.UI.Views;

public partial class SettingsWindow
{
    private const int WheelGestureGapMilliseconds = 300;
    private const double WheelLinePixels = 16;
    private ScrollViewer? _wheelTarget;
    private int _wheelTargetTimestamp;

    private void SettingsScroll_PreviewMouseWheel(object sender, MouseWheelEventArgs e)
    {
        if (e.Handled || e.Delta == 0) return;
        var chain = WheelScrollChain(e.OriginalSource as DependencyObject);
        var latched = _wheelTarget != null
            && unchecked(e.Timestamp - _wheelTargetTimestamp) < WheelGestureGapMilliseconds
            && chain.Contains(_wheelTarget);
        var target = latched ? _wheelTarget : chain.FirstOrDefault(viewer => CanWheelScroll(viewer, e.Delta));
        _wheelTarget = target;
        _wheelTargetTimestamp = e.Timestamp;
        e.Handled = true;
        if (target == null) return;
        var lines = SystemParameters.WheelScrollLines;
        var step = target.CanContentScroll ? Math.Max(1, lines) : lines > 0 ? lines * WheelLinePixels : target.ViewportHeight;
        target.ScrollToVerticalOffset(target.VerticalOffset - e.Delta / (double)Mouse.MouseWheelDeltaForOneLine * step);
    }

    private List<ScrollViewer> WheelScrollChain(DependencyObject? source)
    {
        var chain = new List<ScrollViewer>();
        for (var node = source; node != null; node = WheelParent(node))
        {
            if (node is ScrollViewer viewer && viewer.VerticalScrollBarVisibility != ScrollBarVisibility.Disabled) chain.Add(viewer);
            if (ReferenceEquals(node, SettingsScroll)) break;
        }
        return chain;
    }

    private static DependencyObject? WheelParent(DependencyObject node) =>
        node is Visual or System.Windows.Media.Media3D.Visual3D ? VisualTreeHelper.GetParent(node) : LogicalTreeHelper.GetParent(node);

    private static bool CanWheelScroll(ScrollViewer viewer, int delta) =>
        viewer.ScrollableHeight > 0 && (delta > 0 ? viewer.VerticalOffset > 0 : viewer.VerticalOffset < viewer.ScrollableHeight);
}
