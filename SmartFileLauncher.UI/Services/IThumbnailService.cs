using System.Windows.Media;

namespace SmartFileLauncher.UI.Services;

public sealed record ThumbnailDiagnostics(
    int MemoryCacheCount,
    int MemoryCacheLimit,
    long Requests,
    long MemoryHits,
    long DiskHits,
    long ShellGenerated,
    long Failures,
    int LastDecodedPixelWidth,
    int LastDecodedPixelHeight,
    long DecodedBytes);

public interface IThumbnailService
{
    Task<ImageSource?> GetThumbnailAsync(
        string path,
        int size,
        CancellationToken token = default);

    ThumbnailDiagnostics GetDiagnostics();
}
