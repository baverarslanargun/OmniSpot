using SmartFileLauncher.Core.Application.Settings;
using Xunit;

namespace SmartFileLauncher.Core.Tests.Application.Settings;

public sealed class SettingsApplicationServiceTests
{
    [Fact]
    public void Save_PersistsBeforeApplyingStartupRegistration()
    {
        var calls = new List<string>();
        var store = new RecordingSettingsStore(calls);
        var startup = new RecordingStartupRegistration(calls);
        var service = new SettingsApplicationService(store, startup);
        var settings = new AppSettings { StartWithWindows = true };

        service.Save(settings);

        Assert.Equal(["save", "startup:True"], calls);
        Assert.Same(settings, store.SavedSettings);
    }

    [Fact]
    public void JsonStore_RoundTripsSettings()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            var store = new JsonSettingsStore(path);
            var expected = new AppSettings
            {
                HotkeyModifiers = 6,
                HotkeyKey = 0x41,
                StartMinimized = true,
                StartWithWindows = true,
                MinimizeToTrayOnClose = false,
                NaturalLanguageModeEnabled = true,
                GridViewEnabled = true,
                SearchDebounceMs = 250
            };

            store.Save(expected);
            var actual = store.Load();

            Assert.Equal(expected.HotkeyModifiers, actual.HotkeyModifiers);
            Assert.Equal(expected.HotkeyKey, actual.HotkeyKey);
            Assert.Equal(expected.StartMinimized, actual.StartMinimized);
            Assert.Equal(expected.StartWithWindows, actual.StartWithWindows);
            Assert.Equal(
                expected.MinimizeToTrayOnClose,
                actual.MinimizeToTrayOnClose);
            Assert.Equal(
                expected.NaturalLanguageModeEnabled,
                actual.NaturalLanguageModeEnabled);
            Assert.Equal(expected.GridViewEnabled, actual.GridViewEnabled);
            Assert.Equal(expected.SearchDebounceMs, actual.SearchDebounceMs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void JsonStore_ReturnsCurrentDefaultsForInvalidJson()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var path = Path.Combine(directory, "settings.json");
            File.WriteAllText(path, "{ invalid json");
            var store = new JsonSettingsStore(path);

            var settings = store.Load();

            Assert.Equal((uint)2, settings.HotkeyModifiers);
            Assert.Equal((uint)0x20, settings.HotkeyKey);
            Assert.True(settings.MinimizeToTrayOnClose);
            Assert.Equal(1200, settings.SearchDebounceMs);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void JsonStore_PreservesSilentFailureBehaviorForUnwritableTarget()
    {
        var directory = CreateTemporaryDirectory();
        try
        {
            var store = new JsonSettingsStore(directory);

            store.Save(new AppSettings());

            Assert.True(Directory.Exists(directory));
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static string CreateTemporaryDirectory()
    {
        var directory = Path.Combine(
            Path.GetTempPath(),
            "OmniSpotTests",
            Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private sealed class RecordingSettingsStore(
        List<string> calls) : ISettingsStore
    {
        public AppSettings? SavedSettings { get; private set; }

        public AppSettings Load()
        {
            return new AppSettings();
        }

        public void Save(AppSettings settings)
        {
            calls.Add("save");
            SavedSettings = settings;
        }
    }

    private sealed class RecordingStartupRegistration(
        List<string> calls) : IStartupRegistration
    {
        public void Apply(bool enabled)
        {
            calls.Add($"startup:{enabled}");
        }
    }
}
