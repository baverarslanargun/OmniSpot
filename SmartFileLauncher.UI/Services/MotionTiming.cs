using System.Windows;
using System.Windows.Media.Animation;

namespace SmartFileLauncher.UI.Services;

internal static class MotionTiming
{
    internal static readonly TimeSpan Length = TimeSpan.FromMilliseconds(200);

    internal static Duration Duration => new(Length);

    internal static double Ease(double progress) => 1 - Math.Pow(1 - Math.Clamp(progress, 0, 1), 3);

    internal static IEasingFunction Easing() => new CubicEase { EasingMode = EasingMode.EaseOut };
}
