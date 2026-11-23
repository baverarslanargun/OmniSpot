using System.Windows.Media;

namespace SmartFileLauncher.UI.Services;

public interface IStartMenuShell
{
    IReadOnlyList<StartMenuApplication> ReadApplications(CancellationToken cancellationToken);
    void Launch(StartMenuApplication app);
    ImageSource? LoadIcon(StartMenuApplication app);
    IDisposable Watch(IntPtr window, Action changed);
}

internal interface IStartMenuChangeWatch : IDisposable
{
    bool IsComplete { get; }
}

internal sealed record StartMenuItemChange(string? RemovedId, StartMenuApplication? Item);
internal delegate StartMenuItemChange? ResolveStartMenuChange();

internal interface IStartMenuApplicationActions
{
    void RunAsAdministrator(StartMenuApplication app);
    void OpenFileLocation(StartMenuApplication app);
    void OpenInstalledApplications();
}

internal interface IStartMenuItemNotifications
{
    IDisposable WatchItems(IntPtr window, Action<ResolveStartMenuChange?> changed);
    StartMenuApplication? ReadItem(string id);
}
