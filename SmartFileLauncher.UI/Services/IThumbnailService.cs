using System.Windows.Media;

namespace SmartFileLauncher.UI.Services;

public interface IThumbnailService
{
    Task<ImageSource?> GetThumbnailAsync(
        string path,
        int size,
        CancellationToken token = default);
}
