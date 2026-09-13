using System.Globalization;
using SmartFileLauncher.Core.Services;

namespace SmartFileLauncher.UI.Views;

internal static class IndexProgressPresentation
{
    public static string Format(IndexProgress progress, long elapsedMs, IFormatProvider? culture = null)
    {
        culture ??= CultureInfo.CurrentCulture;
        var details = new List<string>();
        if (progress.TotalItemCount > 0)
            details.Add($"{progress.ItemCount.ToString("N0", culture)}/{progress.TotalItemCount.ToString("N0", culture)} kayıt");
        else if (progress.ItemCount > 0)
            details.Add($"{progress.ItemCount.ToString("N0", culture)} kayıt");
        if (!progress.IsIndeterminate)
            details.Add($"Bu aşama %{Math.Clamp(progress.Percentage, 0, 100)}");
        var elapsed = TimeSpan.FromMilliseconds(Math.Max(0, elapsedMs));
        details.Add($"{(long)elapsed.TotalMinutes:00}:{elapsed.Seconds:00}");
        return progress.Status + Environment.NewLine + string.Join(" · ", details);
    }
}
