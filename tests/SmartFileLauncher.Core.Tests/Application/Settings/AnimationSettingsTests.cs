using System.Text.Json;
using SmartFileLauncher.Core.Application.Settings;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Settings;

public sealed class AnimationSettingsTests
{
    [Fact]
    public void OlderSettingsKeepAnimationsOnAndResetRestoresDefault()
    {
        var settings = JsonSerializer.Deserialize<AppSettings>("{\"GridViewEnabled\":true}")!;
        Assert.True(settings.AnimationsEnabled);

        settings.AnimationsEnabled = false;
        var saved = JsonSerializer.Serialize(settings);
        Assert.False(JsonSerializer.Deserialize<AppSettings>(saved)!.AnimationsEnabled);

        settings.ResetToDefaults();
        Assert.True(settings.AnimationsEnabled);
    }
}
