namespace SmartFileLauncher.Core.Application.Settings;

public sealed class SettingsApplicationService : ISettingsApplicationService
{
    private readonly ISettingsStore _store;
    private readonly IStartupRegistration _startupRegistration;

    public SettingsApplicationService(
        ISettingsStore store,
        IStartupRegistration startupRegistration)
    {
        _store = store ?? throw new ArgumentNullException(nameof(store));
        _startupRegistration = startupRegistration
            ?? throw new ArgumentNullException(nameof(startupRegistration));
    }

    public AppSettings Load()
    {
        return _store.Load();
    }

    public void Save(AppSettings settings)
    {
        ArgumentNullException.ThrowIfNull(settings);
        _store.Save(settings);
        _startupRegistration.Apply(settings.StartWithWindows);
    }
}
