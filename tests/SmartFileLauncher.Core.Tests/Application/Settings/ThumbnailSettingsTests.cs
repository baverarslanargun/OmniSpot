using System.Text.Json;
using SmartFileLauncher.Core.Application.Settings;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Settings;

public sealed class ThumbnailSettingsTests
{
    [Fact]
    public void OlderSettingsReceiveThumbnailDefaults()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"GridViewEnabled\":true}")!;
        Assert.True(settings.ThumbnailPreviewsEnabled);
        Assert.False(settings.ThumbnailCacheUseRamRatio);
        Assert.Equal(1000, settings.ThumbnailCacheMaxCount);
        Assert.Equal(60, settings.ThumbnailIdleSeconds);
        Assert.Empty(settings.ThumbnailPinnedFolders);
        Assert.False(settings.HideThumbnailPinWarning);
        Assert.True(settings.GridViewEnabled);
    }

    [Fact]
    public void ThumbnailSettingsRoundTripAndReset()
    {
        var expected = new AppSettings
        {
            ThumbnailPreviewsEnabled = false,
            ThumbnailCacheUseRamRatio = true,
            ThumbnailCacheMaxCount = 500,
            ThumbnailCacheRamPercent = 0.25,
            ThumbnailIdleSeconds = 120,
            ThumbnailPinnedFolders = new() { @"C:\Images" },
            HideThumbnailPinWarning = true
        };
        var actual = JsonSerializer.Deserialize<AppSettings>(JsonSerializer.Serialize(expected))!;
        Assert.False(actual.ThumbnailPreviewsEnabled);
        Assert.True(actual.ThumbnailCacheUseRamRatio);
        Assert.Equal(500, actual.ThumbnailCacheMaxCount);
        Assert.Equal(0.25, actual.ThumbnailCacheRamPercent);
        Assert.Equal(120, actual.ThumbnailIdleSeconds);
        Assert.Equal(expected.ThumbnailPinnedFolders, actual.ThumbnailPinnedFolders);
        Assert.True(actual.HideThumbnailPinWarning);
        actual.ResetToDefaults();
        Assert.True(actual.ThumbnailPreviewsEnabled);
        Assert.False(actual.ThumbnailCacheUseRamRatio);
        Assert.Equal(1000, actual.ThumbnailCacheMaxCount);
        Assert.Equal(0.1, actual.ThumbnailCacheRamPercent);
        Assert.Equal(60, actual.ThumbnailIdleSeconds);
        Assert.Empty(actual.ThumbnailPinnedFolders);
        Assert.False(actual.HideThumbnailPinWarning);
    }
}
