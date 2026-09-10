using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;

namespace SmartFileLauncher.UI.Services;

public static class ThumbnailFraming
{
    public static bool IsOpaque(ImageSource? source)
    {
        if (source is not BitmapSource bitmap || bitmap.PixelWidth < 2 || bitmap.PixelHeight < 2) return false;
        var format = bitmap.Format;
        if (format != PixelFormats.Bgra32 && format != PixelFormats.Pbgra32) return true;
        try
        {
            var buffer = new byte[4];
            var w = bitmap.PixelWidth - 1;
            var h = bitmap.PixelHeight - 1;
            foreach (var (x, y) in new[] { (0, 0), (w, 0), (0, h), (w, h) })
            {
                bitmap.CopyPixels(new Int32Rect(x, y, 1, 1), buffer, 4, 0);
                if (buffer[3] < 250) return false;
            }
            return true;
        }
        catch
        {
            return false;
        }
    }
}
