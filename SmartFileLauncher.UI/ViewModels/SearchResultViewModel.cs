using System.ComponentModel;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaImageSource = System.Windows.Media.ImageSource;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SmartFileLauncher.UI.ViewModels;

public class SearchResultViewModel : INotifyPropertyChanged
{
    public string Name { get; set; } = "";
    public string FullPath { get; set; } = "";
    public double Score { get; set; }
    public string Icon { get; set; } = "📄";
    public bool IsDirectory { get; set; }

    private static MediaBrush GetFrozenBrush(byte r, byte g, byte b)
    {
        var brush = new MediaSolidColorBrush(MediaColor.FromRgb(r, g, b));
        brush.Freeze();
        return brush;
    }

    private static readonly MediaBrush DefaultFolderColor = GetFrozenBrush(99, 102, 241);
    private static readonly MediaBrush DefaultFolderColorLight = GetFrozenBrush(129, 140, 248);

    public MediaBrush FolderColor { get; set; } = DefaultFolderColor;
    public MediaBrush FolderColorLight { get; set; } = DefaultFolderColorLight;

    private MediaImageSource? _thumbnail;

    public MediaImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Thumbnail)));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public void SetFolderColors(string folderName)
    {
        var name = folderName.ToLowerInvariant();

        if (name.Contains("document") || name.Contains("belgeler") || name == "documents")
        {
            FolderColor = GetFrozenBrush(59, 130, 246);
            FolderColorLight = GetFrozenBrush(96, 165, 250);
        }
        else if (name.Contains("download") || name.Contains("indirilenler") || name == "downloads")
        {
            FolderColor = GetFrozenBrush(16, 185, 129);
            FolderColorLight = GetFrozenBrush(52, 211, 153);
        }
        else if (name.Contains("desktop") || name.Contains("masaüstü") || name == "masaüstü")
        {
            FolderColor = GetFrozenBrush(139, 92, 246);
            FolderColorLight = GetFrozenBrush(167, 139, 250);
        }
        else if (name.Contains("music") || name.Contains("müzik") || name == "music")
        {
            FolderColor = GetFrozenBrush(236, 72, 153);
            FolderColorLight = GetFrozenBrush(244, 114, 182);
        }
        else if (name.Contains("picture") || name.Contains("resim") || name == "pictures")
        {
            FolderColor = GetFrozenBrush(245, 158, 11);
            FolderColorLight = GetFrozenBrush(251, 191, 36);
        }
        else if (name.Contains("video") || name.Contains("videolar") || name == "videos")
        {
            FolderColor = GetFrozenBrush(239, 68, 68);
            FolderColorLight = GetFrozenBrush(248, 113, 113);
        }
        else
        {
            FolderColor = DefaultFolderColor;
            FolderColorLight = DefaultFolderColorLight;
        }
    }
}
