using System.Windows;
using System.Windows.Automation;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Input;
using System.Windows.Media;
using Brush = System.Windows.Media.Brush;
using Brushes = System.Windows.Media.Brushes;
using Color = System.Windows.Media.Color;
using HorizontalAlignment = System.Windows.HorizontalAlignment;
using KeyEventArgs = System.Windows.Input.KeyEventArgs;
using Size = System.Windows.Size;
using TextBox = System.Windows.Controls.TextBox;

namespace SmartFileLauncher.UI.Views;

internal sealed class InlineNameEditor : Adorner
{
    private const double HorizontalInset = 7;
    private const double MinimumWidth = 72;
    private static readonly Brush Ink = Frozen(0xFF, 0x1E, 0x29, 0x3B);
    private static readonly Brush Accent = Frozen(0xFF, 0x63, 0x66, 0xF1);
    private static readonly Brush AccentRing = Frozen(0x29, 0x63, 0x66, 0xF1);
    private static readonly Brush Selection = Frozen(0xFF, 0x81, 0x8C, 0xF8);
    private static readonly Brush Danger = Frozen(0xFF, 0xDC, 0x26, 0x26);
    private static readonly Brush DangerRing = Frozen(0x1F, 0xDC, 0x26, 0x26);
    private static readonly Brush DangerInk = Frozen(0xFF, 0xB9, 0x1C, 0x1C);

    private readonly TextBlock _anchor;
    private readonly StackPanel _root;
    private readonly Border _ring;
    private readonly Border _field;
    private readonly TextBlock _error;
    private readonly int _selectionLength;
    private bool _closed;

    internal InlineNameEditor(UIElement adorned, TextBlock anchor, string text, int selectionLength) : base(adorned)
    {
        _anchor = anchor;
        _selectionLength = selectionLength;
        var alignment = anchor.TextAlignment == TextAlignment.Center ? TextAlignment.Center : TextAlignment.Left;
        Box = new TextBox
        {
            Text = text,
            FontFamily = anchor.FontFamily,
            FontSize = anchor.FontSize,
            FontWeight = anchor.FontWeight,
            Foreground = Ink,
            Background = Brushes.Transparent,
            BorderThickness = new Thickness(0),
            Padding = new Thickness(3, 2, 3, 2),
            TextAlignment = alignment,
            VerticalContentAlignment = VerticalAlignment.Center,
            SelectionBrush = Selection,
            CaretBrush = Ink,
            AcceptsReturn = false,
            AcceptsTab = false
        };
        AutomationProperties.SetName(Box, "Yeni ad");
        _field = new Border
        {
            Child = Box,
            Background = Brushes.White,
            BorderBrush = Accent,
            BorderThickness = new Thickness(1),
            CornerRadius = new CornerRadius(6)
        };
        _ring = new Border
        {
            Child = _field,
            BorderBrush = AccentRing,
            BorderThickness = new Thickness(3),
            CornerRadius = new CornerRadius(9),
            Margin = new Thickness(-3)
        };
        _error = new TextBlock
        {
            Foreground = DangerInk,
            FontSize = 11,
            TextWrapping = TextWrapping.Wrap,
            TextAlignment = alignment,
            Margin = new Thickness(2, 5, 2, 0),
            Visibility = Visibility.Collapsed
        };
        _root = new StackPanel { HorizontalAlignment = HorizontalAlignment.Stretch };
        _root.Children.Add(_ring);
        _root.Children.Add(_error);
        AddVisualChild(_root);

        Box.PreviewKeyDown += OnPreviewKeyDown;
        Box.TextChanged += (_, _) => ClearError();
        Box.LostKeyboardFocus += (_, _) =>
        {
            if (!_closed && !Box.IsKeyboardFocusWithin) Cancelled?.Invoke(this, false);
        };
        Box.Loaded += (_, _) => FocusText();
    }

    internal TextBox Box { get; }

    internal event EventHandler<string>? Submitted;

    internal event EventHandler<bool>? Cancelled;

    protected override int VisualChildrenCount => 1;

    protected override Visual GetVisualChild(int index) => _root;

    internal bool Contains(object? source) =>
        source is Visual visual && (ReferenceEquals(visual, this) || IsAncestorOf(visual));

    internal void ShowError(string message)
    {
        _error.Text = message;
        _error.Visibility = Visibility.Visible;
        _field.BorderBrush = Danger;
        _ring.BorderBrush = DangerRing;
        InvalidateMeasure();
        if (!Box.IsKeyboardFocusWithin) Box.Focus();
    }

    internal void Close() => _closed = true;

    protected override Size MeasureOverride(Size constraint)
    {
        var width = EditorWidth(AnchorBounds());
        _root.Measure(new Size(width, double.PositiveInfinity));
        return new Size(width, _root.DesiredSize.Height);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var bounds = AnchorBounds();
        var width = EditorWidth(bounds);
        var lineHeight = double.IsNaN(_anchor.LineHeight)
            ? _anchor.FontSize * _anchor.FontFamily.LineSpacing
            : _anchor.LineHeight;
        var lineCenter = bounds.Y + Math.Min(bounds.Height, lineHeight) / 2;
        var top = lineCenter - _ring.DesiredSize.Height / 2;
        _root.Arrange(new Rect(bounds.X - HorizontalInset, top, width, _root.DesiredSize.Height));
        return finalSize;
    }

    private static double EditorWidth(Rect bounds) =>
        Math.Max(bounds.Width, MinimumWidth) + 2 * HorizontalInset;

    private Rect AnchorBounds()
    {
        if (!_anchor.IsVisible) return new Rect(0, 0, MinimumWidth, 0);
        try
        {
            return _anchor.TransformToAncestor(AdornedElement).TransformBounds(new Rect(_anchor.RenderSize));
        }
        catch (InvalidOperationException)
        {
            return new Rect(0, 0, MinimumWidth, 0);
        }
    }

    private void FocusText()
    {
        if (_closed) return;
        Box.Focus();
        Box.Select(0, Math.Clamp(_selectionLength, 0, Box.Text.Length));
    }

    private void OnPreviewKeyDown(object sender, KeyEventArgs e)
    {
        switch (e.Key)
        {
            case Key.Enter:
                e.Handled = true;
                Submitted?.Invoke(this, Box.Text);
                break;
            case Key.Escape:
                e.Handled = true;
                Cancelled?.Invoke(this, true);
                break;
            case Key.Up or Key.Down or Key.PageUp or Key.PageDown or Key.Tab:
                e.Handled = true;
                break;
        }
    }

    private void ClearError()
    {
        if (_error.Visibility != Visibility.Visible) return;
        _error.Visibility = Visibility.Collapsed;
        _field.BorderBrush = Accent;
        _ring.BorderBrush = AccentRing;
        InvalidateMeasure();
    }

    private static Brush Frozen(byte a, byte r, byte g, byte b)
    {
        var brush = new SolidColorBrush(Color.FromArgb(a, r, g, b));
        brush.Freeze();
        return brush;
    }
}
