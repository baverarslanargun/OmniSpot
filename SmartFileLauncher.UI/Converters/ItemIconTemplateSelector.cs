using System.Windows;
using System.Windows.Controls;
using SmartFileLauncher.UI.ViewModels;

namespace SmartFileLauncher.UI.Converters;

public class ItemIconTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Folder { get; set; }
    public DataTemplate? File { get; set; }

    public override DataTemplate? SelectTemplate(object item, DependencyObject container)
    {
        var isDirectory = item switch
        {
            DesktopIconViewModel desktop => desktop.IsDirectory,
            SearchResultViewModel result => result.IsDirectory,
            _ => false,
        };
        return isDirectory ? Folder : File;
    }
}
