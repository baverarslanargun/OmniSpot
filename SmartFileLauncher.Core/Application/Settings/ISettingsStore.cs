namespace SmartFileLauncher.Core.Application.Settings;

public interface ISettingsStore
{
    AppSettings Load();
    void Save(AppSettings settings);
}
