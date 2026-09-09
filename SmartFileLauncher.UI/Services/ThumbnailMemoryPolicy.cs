using System.IO;
using SmartFileLauncher.Core.Application.Settings;

namespace SmartFileLauncher.UI.Services;

public readonly record struct SystemMemorySnapshot(long TotalBytes, long AvailableBytes, long ProcessBytes);

public sealed record ThumbnailCacheConfiguration(bool Enabled, int MaxCount, long MaxBytes, string[] PinnedFolders);

public readonly record struct ThumbnailMemoryBudget(long SafeMaxBytes, int SafeMaxCount, double SafeMaxPercent, int Count, long Bytes);

public static class ThumbnailMemoryPolicy
{
    public const long BytesPerThumbnail = 128L * 128 * 4;
    public const long AbsoluteMaxBytes = 512L * 1024 * 1024;

    public static ThumbnailMemoryBudget Calculate(AppSettings settings, SystemMemorySnapshot memory)
    {
        var total = Math.Max(0, memory.TotalBytes);
        var available = Math.Clamp(memory.AvailableBytes, 0, total);
        var safe = Math.Min(AbsoluteMaxBytes, Math.Min(total / 50, available / 10));
        var safeCount = (int)(safe / BytesPerThumbnail);
        var safePercent = total == 0 ? 0 : 100d * safe / total;
        var percent = double.IsFinite(settings.ThumbnailCacheRamPercent)
            ? Math.Clamp(settings.ThumbnailCacheRamPercent, 0, safePercent)
            : 0;
        var count = Math.Clamp(settings.ThumbnailCacheMaxCount, 0, safeCount);
        var bytes = settings.ThumbnailCacheUseRamRatio
            ? Math.Min(safe, (long)(total * (percent / 100d)))
            : count * BytesPerThumbnail;
        if (settings.ThumbnailCacheUseRamRatio)
            count = (int)(bytes / BytesPerThumbnail);
        if (!settings.ThumbnailPreviewsEnabled)
            (count, bytes) = (0, 0);
        return new(safe, safeCount, safePercent, count, bytes);
    }

    public static ThumbnailCacheConfiguration Configure(AppSettings settings, SystemMemorySnapshot memory)
    {
        var budget = Calculate(settings, memory);
        return new(settings.ThumbnailPreviewsEnabled, budget.Count, budget.Bytes,
            (settings.ThumbnailPinnedFolders ?? new()).Select(NormalizeFolder)
                .Where(path => path != null).Cast<string>()
                .Distinct(StringComparer.OrdinalIgnoreCase).ToArray());
    }

    public static int IdleSeconds(AppSettings settings) => Math.Clamp(settings.ThumbnailIdleSeconds, 5, 86400);

    public static string? NormalizeFolder(string? path)
    {
        if (string.IsNullOrWhiteSpace(path) || !Path.IsPathFullyQualified(path)) return null;
        try { return Path.TrimEndingDirectorySeparator(Path.GetFullPath(path)); }
        catch (Exception ex) when (ex is ArgumentException or NotSupportedException or PathTooLongException)
        { return null; }
    }

    public static bool IsPinned(string path, IEnumerable<string> folders)
    {
        var normalized = NormalizeFolder(path);
        if (normalized == null) return false;
        return folders.Any(folder => string.Equals(normalized, folder, StringComparison.OrdinalIgnoreCase)
            || normalized.StartsWith(Path.EndsInDirectorySeparator(folder) ? folder : folder + Path.DirectorySeparatorChar,
                StringComparison.OrdinalIgnoreCase));
    }
}
