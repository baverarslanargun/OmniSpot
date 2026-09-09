using System.Windows.Media;

namespace SmartFileLauncher.UI.Services;

public interface IThumbnailCacheControl
{
    event Action? CacheTrimmed;
    void Configure(ThumbnailCacheConfiguration configuration);
    void EnterIdle();
    void Resume();
    bool IsRetained(ImageSource image);
}
