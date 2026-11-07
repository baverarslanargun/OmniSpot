using System.Windows;

namespace SmartFileLauncher.UI.Services;

internal static class UiAnimationScope
{
    public static readonly DependencyProperty ThumbnailFadeEnabledProperty =
        DependencyProperty.RegisterAttached(
            "ThumbnailFadeEnabled", typeof(bool), typeof(UiAnimationScope),
            new FrameworkPropertyMetadata(true, FrameworkPropertyMetadataOptions.Inherits));

    public static bool GetThumbnailFadeEnabled(DependencyObject element) =>
        (bool)element.GetValue(ThumbnailFadeEnabledProperty);

    public static void SetThumbnailFadeEnabled(DependencyObject element, bool value) =>
        element.SetValue(ThumbnailFadeEnabledProperty, value);
}
