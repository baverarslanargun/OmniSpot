using System.Windows;
using System.Windows.Controls;
using Size = System.Windows.Size;

namespace SmartFileLauncher.UI.Views;

public sealed class ApplicationChipPanel : System.Windows.Controls.Panel
{
    protected override Size MeasureOverride(Size availableSize)
    {
        var unbounded = new Size(double.PositiveInfinity, availableSize.Height);
        var widths = new double[InternalChildren.Count];
        for (var index = 0; index < InternalChildren.Count; index++)
        {
            InternalChildren[index].Measure(unbounded);
            widths[index] = InternalChildren[index].DesiredSize.Width;
        }

        var cap = ShareCap(widths, availableSize.Width);
        double width = 0;
        double height = 0;
        foreach (UIElement child in InternalChildren)
        {
            if (child.DesiredSize.Width > cap) child.Measure(new Size(cap, availableSize.Height));
            width += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        double x = 0;
        foreach (UIElement child in InternalChildren)
        {
            child.Arrange(new Rect(x, 0, child.DesiredSize.Width, finalSize.Height));
            x += child.DesiredSize.Width;
        }
        return finalSize;
    }

    internal static double ShareCap(IReadOnlyList<double> widths, double available)
    {
        if (double.IsInfinity(available)) return double.PositiveInfinity;
        var sorted = widths.Where(width => width > 0).Order().ToArray();
        var remaining = Math.Max(0, available);
        for (var index = 0; index < sorted.Length; index++)
        {
            var share = Math.Floor(remaining / (sorted.Length - index));
            if (sorted[index] > share) return share;
            remaining -= sorted[index];
        }
        return double.PositiveInfinity;
    }
}
