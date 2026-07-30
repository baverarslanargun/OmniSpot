namespace SmartFileLauncher.Core.Application.Settings;

public interface ISettingsApplicationService
{
    AppSettings Load();
    void Save(AppSettings settings);
}
