using System.Globalization;
using System.Windows;
using System.Windows.Data;
using System.Windows.Media;

namespace SmartFileLauncher.UI.Converters;

public class BoolToVisibilityConverter : IValueConverter
{
    public bool Invert { get; set; }

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var flag = value is true;
        if (Invert) flag = !flag;
        return flag ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();
}

public class IconKindConverter : IValueConverter
{
    private sealed record IconStyle(string Label, Geometry Glyph, System.Windows.Media.Brush Color, System.Windows.Media.Brush Tint);

    private static readonly Geometry Empty = Geometry.Empty;
    private static readonly Geometry Lines = Freeze("M7.5 12 h9 v1.5 h-9 z M7.5 15.3 h6 v1.5 h-6 z");
    private static readonly Geometry Image = Freeze("M8.4 10.6 a1.5 1.5 0 1 0 3 0 a1.5 1.5 0 1 0 -3 0 z M6.5 18 l3.6 -4.6 l2.4 2.9 l1.9 -2.3 l3.1 4 z");
    private static readonly Geometry Audio = Freeze("M12.2 8.5 l5 -1.2 v2 l-3.4 0.8 v5.4 a2.3 2.3 0 1 1 -1.6 -2.2 z");
    private static readonly Geometry Video = Freeze("M9.6 10 l6.2 3.6 l-6.2 3.6 z");
    private static readonly Geometry Code = Freeze("M9.3 10.3 l1.1 1.1 l-2.1 2.1 l2.1 2.1 l-1.1 1.1 l-3.2 -3.2 z M14.7 10.3 l3.2 3.2 l-3.2 3.2 l-1.1 -1.1 l2.1 -2.1 l-2.1 -2.1 z");
    private static readonly Geometry Link = Freeze("M7.5 12.8 h5.6 l-1.7 -1.7 l1.1 -1.1 l3.6 3.6 l-3.6 3.6 l-1.1 -1.1 l1.7 -1.7 h-5.6 z");
    private static readonly Geometry Web = Freeze("F0 M12 9.2 a4.6 4.6 0 1 0 0 9.2 a4.6 4.6 0 1 0 0 -9.2 z M12 10.7 a3.1 3.1 0 1 1 0 6.2 a3.1 3.1 0 1 1 0 -6.2 z M7.5 13.2 h9 v1.2 h-9 z");
    private static readonly Geometry Gear = Freeze("F0 M12 10.2 a3.6 3.6 0 1 0 0 7.2 a3.6 3.6 0 1 0 0 -7.2 z M12 12.2 a1.6 1.6 0 1 1 0 3.2 a1.6 1.6 0 1 1 0 -3.2 z M11.3 9 h1.4 v1.6 h-1.4 z M11.3 17 h1.4 v1.6 h-1.4 z M7.4 13.1 h1.6 v1.4 h-1.6 z M15 13.1 h1.6 v1.4 h-1.6 z");

    private static readonly Dictionary<string, IconStyle> Styles = new(StringComparer.Ordinal)
    {
        ["pdf"] = Make("PDF", Empty, 0xDC, 0x26, 0x26),
        ["doc"] = Make("DOC", Empty, 0x25, 0x63, 0xEB),
        ["sheet"] = Make("XLS", Empty, 0x05, 0x96, 0x69),
        ["slides"] = Make("PPT", Empty, 0xEA, 0x58, 0x0C),
        ["archive"] = Make("ZIP", Empty, 0xB4, 0x53, 0x09),
        ["text"] = Make("", Lines, 0x64, 0x74, 0x8B),
        ["image"] = Make("", Image, 0x0E, 0xA5, 0xE9),
        ["audio"] = Make("", Audio, 0xDB, 0x27, 0x77),
        ["video"] = Make("", Video, 0x7C, 0x3A, 0xED),
        ["code"] = Make("", Code, 0x4F, 0x46, 0xE5),
        ["link"] = Make("", Link, 0x08, 0x91, 0xB2),
        ["web"] = Make("", Web, 0x25, 0x63, 0xEB),
        ["app"] = Make("", Gear, 0x47, 0x55, 0x69),
        ["file"] = Make("", Lines, 0x94, 0xA3, 0xB8),
    };

    public string Part { get; set; } = "Glyph";

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        var style = value is string kind && Styles.TryGetValue(kind, out var found) ? found : Styles["file"];
        return Part switch
        {
            "Label" => style.Label,
            "Color" => style.Color,
            "Tint" => style.Tint,
            _ => style.Glyph,
        };
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotImplementedException();

    private static IconStyle Make(string label, Geometry glyph, byte r, byte g, byte b)
    {
        var color = new SolidColorBrush(System.Windows.Media.Color.FromRgb(r, g, b));
        color.Freeze();
        var tint = new SolidColorBrush(System.Windows.Media.Color.FromArgb(0x24, r, g, b));
        tint.Freeze();
        return new IconStyle(label, glyph, color, tint);
    }

    private static Geometry Freeze(string data)
    {
        var geometry = Geometry.Parse(data);
        geometry.Freeze();
        return geometry;
    }
}
