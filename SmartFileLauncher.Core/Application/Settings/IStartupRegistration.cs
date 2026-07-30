namespace SmartFileLauncher.Core.Application.Settings;

public interface IStartupRegistration
{
    void Apply(bool enabled);
}
