using System.Globalization;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
using Brushes = System.Windows.Media.Brushes;

namespace SmartFileLauncher.UI.Services;

public static class FileNameTrimming
{
    private const string Ellipsis = "…";

    public static readonly DependencyProperty FileNameProperty = DependencyProperty.RegisterAttached(
        "FileName", typeof(string), typeof(FileNameTrimming), new PropertyMetadata(null, OnFileNameChanged));

    public static string? GetFileName(DependencyObject element) => (string?)element.GetValue(FileNameProperty);

    public static void SetFileName(DependencyObject element, string? value) => element.SetValue(FileNameProperty, value);

    private static void OnFileNameChanged(DependencyObject element, DependencyPropertyChangedEventArgs e)
    {
        if (element is not TextBlock block) return;
        block.SizeChanged -= OnSizeChanged;
        block.SizeChanged += OnSizeChanged;
        Fit(block);
    }

    private static void OnSizeChanged(object sender, SizeChangedEventArgs e)
    {
        if (e.WidthChanged) Fit((TextBlock)sender);
    }

    internal static void Fit(TextBlock block)
    {
        var name = GetFileName(block) ?? "";
        var width = block.ActualWidth;
        var singleLine = block.TextWrapping == TextWrapping.NoWrap;
        if (width <= 0 || !singleLine && double.IsInfinity(block.MaxHeight))
        {
            block.Text = name;
            return;
        }
        var maxHeight = block.MaxHeight;
        block.Text = Trim(name, candidate =>
        {
            if (singleLine) return Format(block, candidate, null).WidthIncludingTrailingWhitespace <= width + 0.5;
            var formatted = Format(block, candidate, width);
            return formatted.Height <= maxHeight + 0.5 && formatted.Width <= width + 0.5;
        });
    }

    internal static double LayoutHeight(TextBlock block, string text) =>
        block.ActualWidth <= 0 ? 0 : Format(block, text, block.ActualWidth).Height;

    private static FormattedText Format(TextBlock block, string text, double? width)
    {
        var formatted = new FormattedText(text, CultureInfo.CurrentUICulture, block.FlowDirection,
            new Typeface(block.FontFamily, block.FontStyle, block.FontWeight, block.FontStretch),
            block.FontSize, Brushes.Black, VisualTreeHelper.GetDpi(block).PixelsPerDip)
        {
            Trimming = TextTrimming.None
        };
        if (width is { } limit) formatted.MaxTextWidth = limit;
        if (!double.IsNaN(block.LineHeight)) formatted.LineHeight = block.LineHeight;
        return formatted;
    }

    internal static string Trim(string name, Func<string, bool> fits)
    {
        if (name.Length < 4 || fits(name)) return name;
        var tail = name[^TailLength(name)..];
        var head = name[..^tail.Length];
        if (!fits(Ellipsis + tail)) return name;

        var keep = 0;
        for (int low = 1, high = head.Length - 1; low <= high;)
        {
            var mid = (low + high) / 2;
            if (fits(Candidate(head, mid, tail)))
            {
                keep = mid;
                low = mid + 1;
            }
            else high = mid - 1;
        }
        return Candidate(head, keep, tail);
    }

    private static string Candidate(string head, int keep, string tail)
    {
        if (keep > 0 && char.IsHighSurrogate(head[keep - 1])) keep--;
        return head[..keep].TrimEnd() + Ellipsis + tail;
    }

    internal static int ExtensionLength(string name)
    {
        var dot = name.LastIndexOf('.');
        if (dot <= 0 || name.Length - dot > 10) return 0;
        var inner = name.LastIndexOf('.', dot - 1);
        return inner > 0 && dot - inner is > 1 and <= 5 ? name.Length - inner : name.Length - dot;
    }

    private static int TailLength(string name)
    {
        var extension = ExtensionLength(name);
        var tail = extension + (extension > 0 ? 4 : 6);
        tail = Math.Min(tail, name.Length / 2);
        if (tail < name.Length && char.IsLowSurrogate(name[^tail])) tail++;
        return tail;
    }
}
