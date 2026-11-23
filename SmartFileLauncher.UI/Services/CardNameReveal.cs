using System.Windows;
using System.Windows.Controls;
using System.Windows.Documents;
using System.Windows.Media;
using System.Windows.Media.Animation;
using MouseEventArgs = System.Windows.Input.MouseEventArgs;
using Point = System.Windows.Point;
using Size = System.Windows.Size;

namespace SmartFileLauncher.UI.Services;

public static class CardNameReveal
{
    internal const string IconPart = "CardIcon";
    internal const string NamePart = "CardName";
    private const string Ellipsis = "…";
    private const double MinimumIconScale = 0.35;
    private const double MaximumNameHeight = 84;
    private const double IconGap = 4;
    [ThreadStatic] private static WeakReference<FrameworkElement>? _current;

    public static readonly DependencyProperty EnabledProperty = DependencyProperty.RegisterAttached(
        "Enabled", typeof(bool), typeof(CardNameReveal), new PropertyMetadata(false, OnEnabledChanged));

    private static readonly DependencyProperty ProgressProperty = DependencyProperty.RegisterAttached(
        "Progress", typeof(double), typeof(CardNameReveal), new PropertyMetadata(0d, OnProgressChanged));

    private static readonly DependencyProperty StateProperty = DependencyProperty.RegisterAttached(
        "State", typeof(RevealState), typeof(CardNameReveal), new PropertyMetadata(null));

    private static readonly DependencyProperty HeldProperty = DependencyProperty.RegisterAttached(
        "Held", typeof(bool), typeof(CardNameReveal), new PropertyMetadata(false));

    public static bool GetEnabled(DependencyObject element) => (bool)element.GetValue(EnabledProperty);

    public static void SetEnabled(DependencyObject element, bool value) => element.SetValue(EnabledProperty, value);

    internal static double Progress(DependencyObject card) => (double)card.GetValue(ProgressProperty);

    internal static string? RevealedText(DependencyObject card) =>
        card.GetValue(StateProperty) is RevealState { Adorner.Text.Text: var text } && Progress(card) > 0 ? text : null;

    internal static void SetCurrent(FrameworkElement? card)
    {
        FrameworkElement? previous = null;
        _current?.TryGetTarget(out previous);
        if (ReferenceEquals(previous, card)) return;
        _current = card == null ? null : new WeakReference<FrameworkElement>(card);
        var animate = UiAnimationSettings.Current.Enabled;
        if (previous != null) Update(previous, animate);
        if (card != null && GetEnabled(card)) Update(card, animate);
    }

    private static bool IsCurrent(FrameworkElement card) =>
        _current != null && _current.TryGetTarget(out var current) && ReferenceEquals(current, card);

    private static void OnEnabledChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not FrameworkElement card) return;
        card.MouseEnter -= OnPointer;
        card.MouseLeave -= OnPointer;
        card.DataContextChanged -= OnDataContextChanged;
        card.Unloaded -= OnUnloaded;
        if (!(bool)e.NewValue) return;
        card.MouseEnter += OnPointer;
        card.MouseLeave += OnPointer;
        card.DataContextChanged += OnDataContextChanged;
        card.Unloaded += OnUnloaded;
    }

    private static void OnPointer(object sender, MouseEventArgs e) =>
        Update((FrameworkElement)sender, UiAnimationSettings.Current.Enabled);

    private static void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e)
    {
        var card = (FrameworkElement)sender;
        if (IsCurrent(card)) _current = null;
        Reset(card);
    }

    private static void OnUnloaded(object sender, RoutedEventArgs e) => Reset((FrameworkElement)sender);

    private static void Update(FrameworkElement card, bool animate)
    {
        if (card.IsMouseOver || IsCurrent(card)) Open(card, animate);
        else Close(card, animate);
    }

    internal static void Hold(FrameworkElement card)
    {
        card.SetValue(HeldProperty, true);
        Reset(card);
    }

    internal static void Release(FrameworkElement card, bool animate)
    {
        if (!IsHeld(card)) return;
        card.ClearValue(HeldProperty);
        if (GetEnabled(card)) Update(card, animate);
    }

    private static bool IsHeld(FrameworkElement card) => (bool)card.GetValue(HeldProperty);

    internal static void Open(FrameworkElement card, bool animate)
    {
        if (IsHeld(card)) return;
        if (Progress(card) <= 0 && Prepare(card) == null) return;
        Run(card, 1, animate);
    }

    internal static void Close(FrameworkElement card, bool animate)
    {
        if (card.GetValue(StateProperty) == null) return;
        Run(card, 0, animate);
    }

    private static void Reset(FrameworkElement card)
    {
        Run(card, 0, false);
        if (card.GetValue(StateProperty) is RevealState state) state.Detach();
        card.ClearValue(StateProperty);
    }

    private static void Run(FrameworkElement card, double to, bool animate)
    {
        if (!animate)
        {
            card.BeginAnimation(ProgressProperty, null);
            card.SetValue(ProgressProperty, to);
            return;
        }
        card.BeginAnimation(ProgressProperty, new DoubleAnimation(to, MotionTiming.Duration) { EasingFunction = MotionTiming.Easing() });
    }

    private static RevealState? Prepare(FrameworkElement card)
    {
        if (card.GetValue(StateProperty) is RevealState stale) stale.Detach();
        card.ClearValue(StateProperty);
        if (Parts(card) is not var (icon, name) || name.ActualWidth <= 0 || icon.ActualHeight <= 0) return null;
        var full = FileNameTrimming.GetFileName(name) ?? name.Text;
        var shown = name.Text;
        if (shown == full) return null;
        if (AdornerLayer.GetAdornerLayer(name) is not { } layer) return null;

        var start = name.ActualHeight;
        var end = Math.Max(start, Math.Min(FileNameTrimming.LayoutHeight(name, full), MaximumNameHeight));
        var iconTop = icon.TranslatePoint(new Point(0, 0), card).Y;
        var nameTop = name.TranslatePoint(new Point(0, 0), card).Y;
        var room = nameTop - iconTop + start - end - IconGap;
        var scale = Math.Clamp(room / icon.ActualHeight, MinimumIconScale, 1);

        var cut = shown.IndexOf(Ellipsis, StringComparison.Ordinal);
        var head = cut >= 0 && full.StartsWith(shown[..cut], StringComparison.Ordinal) ? cut : -1;
        var tail = cut >= 0 ? shown[(cut + Ellipsis.Length)..] : "";
        if (head < 0 || !full.EndsWith(tail, StringComparison.Ordinal) || head + tail.Length > full.Length)
        {
            head = -1;
            tail = "";
        }

        var adorner = new NameAdorner(name, end);
        layer.Add(adorner);
        var transform = icon.RenderTransform as ScaleTransform;
        if (transform == null || transform.IsFrozen)
        {
            transform = new ScaleTransform(1, 1);
            icon.RenderTransform = transform;
        }
        icon.RenderTransformOrigin = new Point(0.5, 0);
        var state = new RevealState(icon, name, layer, adorner, transform, full, shown, head, tail, start, end, scale);
        card.SetValue(StateProperty, state);
        return state;
    }

    private static void OnProgressChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element.GetValue(StateProperty) is not RevealState state) return;
        var progress = Math.Clamp((double)e.NewValue, 0, 1);
        if (progress <= 0)
        {
            state.Adorner.Visibility = Visibility.Collapsed;
            state.Name.Opacity = 1;
            state.Transform.ScaleX = state.Transform.ScaleY = 1;
            state.Detach();
            element.ClearValue(StateProperty);
            return;
        }
        state.Name.Opacity = 0;
        state.Adorner.Visibility = Visibility.Visible;
        state.Adorner.Text.Text = Partial(state, progress);
        state.Adorner.Reveal = state.Start + (state.End - state.Start) * progress;
        state.Transform.ScaleX = state.Transform.ScaleY = 1 + (state.Scale - 1) * progress;
    }

    internal static string Partial(string full, string shown, int head, string tail, double progress)
    {
        if (progress >= 1) return full;
        if (head < 0) return progress <= 0 ? shown : full;
        var hidden = full.Length - head - tail.Length;
        var count = head + (int)Math.Round(hidden * progress);
        if (count > 0 && count < full.Length && char.IsHighSurrogate(full[count - 1])) count--;
        if (count >= full.Length - tail.Length) return full;
        return count <= head ? shown : full[..count] + Ellipsis + tail;
    }

    private static string Partial(RevealState state, double progress) =>
        Partial(state.Full, state.Shown, state.Head, state.Tail, progress);

    private static (FrameworkElement Icon, TextBlock Name)? Parts(FrameworkElement card)
    {
        FrameworkElement? icon = null;
        TextBlock? name = null;
        foreach (var element in Descendants(card))
        {
            if (icon == null && element is FrameworkElement { Name: IconPart } foundIcon) icon = foundIcon;
            else if (name == null && element is TextBlock { Name: NamePart } foundName) name = foundName;
            if (icon != null && name != null) return (icon, name);
        }
        return null;
    }

    private static IEnumerable<DependencyObject> Descendants(DependencyObject root)
    {
        for (var i = 0; i < VisualTreeHelper.GetChildrenCount(root); i++)
        {
            var child = VisualTreeHelper.GetChild(root, i);
            yield return child;
            foreach (var nested in Descendants(child)) yield return nested;
        }
    }

    private sealed record RevealState(
        FrameworkElement Icon, TextBlock Name, AdornerLayer Layer, NameAdorner Adorner, ScaleTransform Transform,
        string Full, string Shown, int Head, string Tail, double Start, double End, double Scale)
    {
        internal void Detach() => Layer.Remove(Adorner);
    }

    private sealed class NameAdorner : Adorner
    {
        private readonly Border _box;
        private double _height;

        internal NameAdorner(TextBlock name, double maximum) : base(name)
        {
            IsHitTestVisible = false;
            Visibility = Visibility.Collapsed;
            Text = new TextBlock
            {
                FontFamily = name.FontFamily,
                FontSize = name.FontSize,
                FontWeight = name.FontWeight,
                FontStyle = name.FontStyle,
                FontStretch = name.FontStretch,
                Foreground = name.Foreground,
                TextAlignment = name.TextAlignment,
                LineHeight = name.LineHeight,
                TextWrapping = TextWrapping.Wrap,
                TextTrimming = TextTrimming.CharacterEllipsis,
                MaxHeight = maximum,
                VerticalAlignment = VerticalAlignment.Top
            };
            _box = new Border { ClipToBounds = true, Child = Text };
            AddVisualChild(_box);
        }

        internal TextBlock Text { get; }

        internal double Reveal
        {
            get => _height;
            set
            {
                _height = value;
                InvalidateArrange();
            }
        }

        protected override int VisualChildrenCount => 1;

        protected override Visual GetVisualChild(int index) => _box;

        protected override Size MeasureOverride(Size constraint)
        {
            var width = AdornedElement.RenderSize.Width;
            _box.Measure(new Size(width, double.PositiveInfinity));
            return new Size(width, _height);
        }

        protected override Size ArrangeOverride(Size finalSize)
        {
            var element = AdornedElement.RenderSize;
            _box.Arrange(new Rect(0, element.Height - _height, element.Width, _height));
            return finalSize;
        }
    }
}
