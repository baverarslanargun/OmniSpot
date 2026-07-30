using System.ComponentModel;
using MediaBrush = System.Windows.Media.Brush;
using MediaColor = System.Windows.Media.Color;
using MediaImageSource = System.Windows.Media.ImageSource;
using MediaSolidColorBrush = System.Windows.Media.SolidColorBrush;

namespace SmartFileLauncher.UI.ViewModels;

public class DesktopIconViewModel : INotifyPropertyChanged
{
    private string _name = "";
    private string _fullPath = "";
    private string _icon = "📄";
    private bool _isDirectory;
    private bool _isCut;
    private double _opacity = 1.0;

    private static readonly Dictionary<string, MediaBrush> _brushCache = new();
    private static readonly object _brushCacheLock = new();

    private static MediaBrush GetOrCreateFrozenBrush(byte r, byte g, byte b)
    {
        var key = $"{r},{g},{b}";
        lock (_brushCacheLock)
        {
            if (_brushCache.TryGetValue(key, out var cached))
            {
                return cached;
            }

            var brush = new MediaSolidColorBrush(MediaColor.FromRgb(r, g, b));
            brush.Freeze();
            _brushCache[key] = brush;
            return brush;
        }
    }

    public string Name
    {
        get => _name;
        set
        {
            if (_name != value)
            {
                _name = value;
                OnPropertyChanged(nameof(Name));
            }
        }
    }

    public string FullPath
    {
        get => _fullPath;
        set
        {
            if (_fullPath != value)
            {
                _fullPath = value;
                OnPropertyChanged(nameof(FullPath));
            }
        }
    }

    public string Icon
    {
        get => _icon;
        set
        {
            if (_icon != value)
            {
                _icon = value;
                OnPropertyChanged(nameof(Icon));
            }
        }
    }

    public bool IsDirectory
    {
        get => _isDirectory;
        set
        {
            if (_isDirectory != value)
            {
                _isDirectory = value;
                OnPropertyChanged(nameof(IsDirectory));
            }
        }
    }

    public bool IsCut
    {
        get => _isCut;
        set
        {
            if (_isCut != value)
            {
                _isCut = value;
                Opacity = value ? 0.5 : 1.0;
                OnPropertyChanged(nameof(IsCut));
            }
        }
    }

    public double Opacity
    {
        get => _opacity;
        set
        {
            if (_opacity != value)
            {
                _opacity = value;
                OnPropertyChanged(nameof(Opacity));
            }
        }
    }

    private MediaBrush _folderColor = GetOrCreateFrozenBrush(99, 102, 241);
    private MediaBrush _folderColorLight = GetOrCreateFrozenBrush(129, 140, 248);

    public MediaBrush FolderColor
    {
        get => _folderColor;
        set
        {
            if (_folderColor != value)
            {
                _folderColor = value;
                OnPropertyChanged(nameof(FolderColor));
            }
        }
    }

    public MediaBrush FolderColorLight
    {
        get => _folderColorLight;
        set
        {
            if (_folderColorLight != value)
            {
                _folderColorLight = value;
                OnPropertyChanged(nameof(FolderColorLight));
            }
        }
    }

    private MediaImageSource? _thumbnail;

    public MediaImageSource? Thumbnail
    {
        get => _thumbnail;
        set
        {
            if (_thumbnail != value)
            {
                _thumbnail = value;
                OnPropertyChanged(nameof(Thumbnail));
            }
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged(string propertyName)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }

    public void SetFolderColors(string folderName)
    {
        var name = folderName.ToLowerInvariant();

        if (name.Contains("document") || name.Contains("belgeler") || name == "documents")
        {
            FolderColor = GetOrCreateFrozenBrush(59, 130, 246);
            FolderColorLight = GetOrCreateFrozenBrush(96, 165, 250);
        }
        else if (name.Contains("download") || name.Contains("indirilenler") || name == "downloads")
        {
            FolderColor = GetOrCreateFrozenBrush(16, 185, 129);
            FolderColorLight = GetOrCreateFrozenBrush(52, 211, 153);
        }
        else if (name.Contains("desktop") || name.Contains("masaüstü") || name == "masaüstü")
        {
            FolderColor = GetOrCreateFrozenBrush(139, 92, 246);
            FolderColorLight = GetOrCreateFrozenBrush(167, 139, 250);
        }
        else if (name.Contains("music") || name.Contains("müzik") || name == "music")
        {
            FolderColor = GetOrCreateFrozenBrush(236, 72, 153);
            FolderColorLight = GetOrCreateFrozenBrush(244, 114, 182);
        }
        else if (name.Contains("picture") || name.Contains("resim") || name == "pictures")
        {
            FolderColor = GetOrCreateFrozenBrush(245, 158, 11);
            FolderColorLight = GetOrCreateFrozenBrush(251, 191, 36);
        }
        else if (name.Contains("video") || name.Contains("videolar") || name == "videos")
        {
            FolderColor = GetOrCreateFrozenBrush(239, 68, 68);
            FolderColorLight = GetOrCreateFrozenBrush(248, 113, 113);
        }
        else
        {
            FolderColor = GetOrCreateFrozenBrush(99, 102, 241);
            FolderColorLight = GetOrCreateFrozenBrush(129, 140, 248);
        }
    }
}
