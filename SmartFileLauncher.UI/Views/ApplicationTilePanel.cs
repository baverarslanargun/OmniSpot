using System.Collections.Specialized;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Media;
using Size = System.Windows.Size;

namespace SmartFileLauncher.UI.Views;

public sealed class ApplicationTilePanel : VirtualizingPanel, IScrollInfo
{
    public static readonly DependencyProperty ColumnsProperty = DependencyProperty.Register(
        nameof(Columns), typeof(int), typeof(ApplicationTilePanel),
        new FrameworkPropertyMetadata(4, FrameworkPropertyMetadataOptions.AffectsMeasure));

    public static readonly DependencyProperty ItemHeightProperty = DependencyProperty.Register(
        nameof(ItemHeight), typeof(double), typeof(ApplicationTilePanel),
        new FrameworkPropertyMetadata(140.0, FrameworkPropertyMetadataOptions.AffectsMeasure));

    private const int CacheRows = 1;
    private const double WheelStep = 48;
    private const double LineStep = 16;
    private Size _extent;
    private Size _viewport;
    private double _offset;

    public int Columns
    {
        get => (int)GetValue(ColumnsProperty);
        set => SetValue(ColumnsProperty, value);
    }

    public double ItemHeight
    {
        get => (double)GetValue(ItemHeightProperty);
        set => SetValue(ItemHeightProperty, value);
    }

    private int ColumnCount => Math.Max(1, Columns);
    private int ItemCount => ItemsControl.GetItemsOwner(this)?.Items.Count ?? 0;

    protected override Size MeasureOverride(Size availableSize)
    {
        var columns = ColumnCount;
        var count = ItemCount;
        var rows = (count + columns - 1) / columns;
        var width = double.IsInfinity(availableSize.Width) ? columns * ItemHeight : availableSize.Width;
        var contentHeight = rows * ItemHeight;
        var height = double.IsInfinity(availableSize.Height) ? contentHeight : Math.Min(availableSize.Height, contentHeight);
        SetScrollInfo(new Size(width, contentHeight), new Size(width, double.IsInfinity(availableSize.Height) ? contentHeight : availableSize.Height));

        var children = InternalChildren;
        var generator = ItemContainerGenerator;
        var (first, last) = VisibleRange(columns, count);
        if (generator != null && last >= first)
        {
            var tile = new Size(width / columns, ItemHeight);
            var start = generator.GeneratorPositionFromIndex(first);
            var childIndex = start.Offset == 0 ? start.Index : start.Index + 1;
            using (generator.StartAt(start, GeneratorDirection.Forward, true))
            {
                for (var index = first; index <= last; index++, childIndex++)
                {
                    if (generator.GenerateNext(out var newlyRealized) is not UIElement child) break;
                    if (newlyRealized)
                    {
                        if (childIndex >= children.Count) AddInternalChild(child);
                        else InsertInternalChild(childIndex, child);
                        generator.PrepareItemContainer(child);
                    }
                    child.Measure(tile);
                }
            }
        }
        RemoveOutside(first, last);
        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        SetScrollInfo(_extent, finalSize);
        var columns = ColumnCount;
        var tileWidth = finalSize.Width / columns;
        var generator = ItemContainerGenerator;
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            var index = generator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index < 0) continue;
            children[i].Arrange(new Rect(index % columns * tileWidth, index / columns * ItemHeight - _offset,
                tileWidth, ItemHeight));
        }
        return finalSize;
    }

    protected override void OnItemsChanged(object sender, ItemsChangedEventArgs args)
    {
        if (args.Action is NotifyCollectionChangedAction.Remove or NotifyCollectionChangedAction.Replace or
            NotifyCollectionChangedAction.Move)
            RemoveInternalChildRange(args.Position.Index, args.ItemUICount);
        base.OnItemsChanged(sender, args);
    }

    protected override void OnClearChildren()
    {
        base.OnClearChildren();
        _offset = 0;
        ScrollOwner?.InvalidateScrollInfo();
    }

    protected override void BringIndexIntoView(int index)
    {
        if (index >= 0) BringRowIntoView(index / ColumnCount);
    }

    private (int First, int Last) VisibleRange(int columns, int count)
    {
        if (count == 0 || ItemHeight <= 0) return (0, -1);
        var rows = (count + columns - 1) / columns;
        var firstRow = Math.Clamp((int)Math.Floor(_offset / ItemHeight) - CacheRows, 0, rows - 1);
        var lastRow = Math.Clamp((int)Math.Ceiling((_offset + _viewport.Height) / ItemHeight) - 1 + CacheRows, firstRow, rows - 1);
        return (firstRow * columns, Math.Min(count - 1, (lastRow + 1) * columns - 1));
    }

    private void RemoveOutside(int first, int last)
    {
        var generator = ItemContainerGenerator;
        if (generator == null) return;
        var children = InternalChildren;
        for (var i = children.Count - 1; i >= 0; i--)
        {
            var position = new GeneratorPosition(i, 0);
            var index = generator.IndexFromGeneratorPosition(position);
            if (index >= first && index <= last) continue;
            generator.Remove(position, 1);
            RemoveInternalChildRange(i, 1);
        }
    }

    private void BringRowIntoView(int row)
    {
        var top = row * ItemHeight;
        var bottom = top + ItemHeight;
        if (top < _offset) SetVerticalOffset(top);
        else if (bottom > _offset + _viewport.Height) SetVerticalOffset(bottom - _viewport.Height);
    }

    private void SetScrollInfo(Size extent, Size viewport)
    {
        var changed = extent != _extent || viewport != _viewport;
        _extent = extent;
        _viewport = viewport;
        var clamped = Math.Clamp(_offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (clamped != _offset)
        {
            _offset = clamped;
            changed = true;
        }
        if (changed) ScrollOwner?.InvalidateScrollInfo();
    }

    public bool CanHorizontallyScroll { get; set; }
    public bool CanVerticallyScroll { get; set; }
    public double ExtentWidth => _extent.Width;
    public double ExtentHeight => _extent.Height;
    public double ViewportWidth => _viewport.Width;
    public double ViewportHeight => _viewport.Height;
    public double HorizontalOffset => 0;
    public double VerticalOffset => _offset;
    public ScrollViewer? ScrollOwner { get; set; }

    public void LineUp() => SetVerticalOffset(_offset - LineStep);
    public void LineDown() => SetVerticalOffset(_offset + LineStep);
    public void PageUp() => SetVerticalOffset(_offset - _viewport.Height);
    public void PageDown() => SetVerticalOffset(_offset + _viewport.Height);
    public void MouseWheelUp() => SetVerticalOffset(_offset - WheelStep);
    public void MouseWheelDown() => SetVerticalOffset(_offset + WheelStep);
    public void LineLeft() { }
    public void LineRight() { }
    public void PageLeft() { }
    public void PageRight() { }
    public void MouseWheelLeft() { }
    public void MouseWheelRight() { }
    public void SetHorizontalOffset(double offset) { }

    public void SetVerticalOffset(double offset)
    {
        var clamped = Math.Clamp(offset, 0, Math.Max(0, _extent.Height - _viewport.Height));
        if (clamped == _offset) return;
        _offset = clamped;
        ScrollOwner?.InvalidateScrollInfo();
        InvalidateMeasure();
    }

    public Rect MakeVisible(Visual visual, Rect rectangle)
    {
        var children = InternalChildren;
        for (var i = 0; i < children.Count; i++)
        {
            if (!ReferenceEquals(children[i], visual) && !children[i].IsAncestorOf(visual)) continue;
            var index = ItemContainerGenerator.IndexFromGeneratorPosition(new GeneratorPosition(i, 0));
            if (index >= 0) BringRowIntoView(index / ColumnCount);
            break;
        }
        return rectangle;
    }
}
