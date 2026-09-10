namespace SmartFileLauncher.UI.ViewModels;

public sealed class DesktopRowViewModel
{
    public DesktopRowViewModel(IReadOnlyList<DesktopIconViewModel> items)
    {
        Items = items;
    }

    public IReadOnlyList<DesktopIconViewModel> Items { get; }
}
